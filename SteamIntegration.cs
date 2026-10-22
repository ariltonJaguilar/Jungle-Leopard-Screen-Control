using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace JungleLeopardPoc;

internal sealed record SteamGameInfo(
    uint AppId,
    string Name,
    string SteamRoot,
    string InstallPath,
    bool IsNonSteamShortcut = false,
    string? ShortcutArguments = null);

internal static class SteamGameDetector
{
    private sealed record InstalledGame(
        uint AppId,
        string Name,
        string SteamRoot,
        string InstallPath,
        string? ShortcutExecutable = null,
        string? ShortcutArguments = null,
        bool IsNonSteamShortcut = false);

    private sealed record ProcessPathInfo(uint ProcessId, string ExecutablePath);

    private readonly record struct ScoredShortcut(InstalledGame Game, int ArgumentCount);

    private const uint SnapshotProcesses = 0x00000002;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmRead = 0x0010;
    private const int ProcessBasicInformationClass = 0;
    private const int ProcessWow64InformationClass = 26;
    private const int MaxShortcutFileBytes = 4 * 1024 * 1024;
    private static readonly object CacheLock = new();
    private static IReadOnlyList<InstalledGame> _installedGames = Array.Empty<InstalledGame>();
    private static DateTime _cacheExpiresAtUtc;

    public static SteamGameInfo? GetForegroundGame() => GetForegroundGame(out _);

    public static SteamGameInfo? GetForegroundGame(out string diagnostic)
    {
        IntPtr window = GetForegroundWindow();
        if (window == IntPtr.Zero)
        {
            diagnostic = "No foreground window is available.";
            return null;
        }

        _ = GetWindowThreadProcessId(window, out uint processId);
        if (processId == 0 || processId == Environment.ProcessId)
        {
            diagnostic = $"Foreground window {window} has no process or belongs to this app (pid {processId}).";
            return null;
        }

        string foregroundTitle = GetWindowTitle(window);
        IReadOnlyList<ProcessPathInfo> foregroundProcesses = GetProcessTree(processId);
        if (foregroundProcesses.Count == 0)
        {
            diagnostic = $"Could not read foreground process path (pid {processId}, title '{foregroundTitle}').";
            return null;
        }

        IReadOnlyList<InstalledGame> games = GetInstalledGames();
        foreach (ProcessPathInfo process in foregroundProcesses)
        {
            foreach (InstalledGame game in games)
            {
                if (!game.IsNonSteamShortcut && IsSameOrChildPath(process.ExecutablePath, game.InstallPath))
                {
                    diagnostic = $"Matched Steam game appid={game.AppId} name='{game.Name}', foregroundPid={processId}, matchedPid={process.ProcessId}, exe='{process.ExecutablePath}', install='{game.InstallPath}', title='{foregroundTitle}'.";
                    return new SteamGameInfo(game.AppId, game.Name, game.SteamRoot, game.InstallPath);
                }
            }
        }

        foreach (ProcessPathInfo process in foregroundProcesses)
        {
            InstalledGame[] matchingShortcuts = games
                .Where(game => game.IsNonSteamShortcut && game.ShortcutExecutable is not null
                    && PathsEqual(process.ExecutablePath, game.ShortcutExecutable))
                .DistinctBy(game => (game.AppId, game.ShortcutExecutable, game.ShortcutArguments))
                .ToArray();
            if (matchingShortcuts.Length == 1)
            {
                diagnostic = $"Matched Steam shortcut appid={matchingShortcuts[0].AppId} name='{matchingShortcuts[0].Name}', foregroundPid={processId}, matchedPid={process.ProcessId}, exe='{process.ExecutablePath}', title='{foregroundTitle}'.";
                return ToShortcutInfo(matchingShortcuts[0]);
            }
            if (matchingShortcuts.Length == 0)
                continue;

            string? commandLine = GetProcessCommandLine(process.ProcessId);
            InstalledGame? commandLineMatch = FindShortcutByArguments(matchingShortcuts, commandLine);
            if (commandLineMatch is not null)
            {
                diagnostic = $"Matched Steam shortcut by launch arguments appid={commandLineMatch.AppId} name='{commandLineMatch.Name}', foregroundPid={processId}, matchedPid={process.ProcessId}, exe='{process.ExecutablePath}', title='{foregroundTitle}'.";
                return ToShortcutInfo(commandLineMatch);
            }

            if (process.ProcessId == processId)
            {
                InstalledGame? titleMatch = FindShortcutByWindowTitle(matchingShortcuts, foregroundTitle);
                if (titleMatch is not null)
                {
                    diagnostic = $"Matched Steam shortcut by window title appid={titleMatch.AppId} name='{titleMatch.Name}', foregroundPid={processId}, exe='{process.ExecutablePath}', title='{foregroundTitle}'.";
                    return ToShortcutInfo(titleMatch);
                }
            }
        }

        string processChain = string.Join(" > ", foregroundProcesses.Select(process => $"{process.ProcessId}:{process.ExecutablePath}"));
        diagnostic = $"No Steam game matched. foregroundPid={processId}, title='{foregroundTitle}', processChain='{processChain}', installedGames={games.Count}, steamRoots='{string.Join("|", games.Select(game => game.SteamRoot).Distinct(StringComparer.OrdinalIgnoreCase))}'.";
        return null;
    }

    private static IReadOnlyList<InstalledGame> GetInstalledGames()
    {
        lock (CacheLock)
        {
            if (DateTime.UtcNow < _cacheExpiresAtUtc)
                return _installedGames;

            _installedGames = ReadInstalledGames();
            _cacheExpiresAtUtc = DateTime.UtcNow.AddSeconds(30);
            return _installedGames;
        }
    }

    private static IReadOnlyList<InstalledGame> ReadInstalledGames()
    {
        var games = new List<InstalledGame>();
        foreach (string steamRoot in GetSteamRoots())
        {
            var libraryRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { steamRoot };
            string libraryFoldersPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            try
            {
                if (File.Exists(libraryFoldersPath))
                {
                    string contents = File.ReadAllText(libraryFoldersPath);
                    foreach (Match match in Regex.Matches(contents, "\\\"path\\\"\\s+\\\"(?<path>[^\\\"]+)\\\"", RegexOptions.IgnoreCase))
                    {
                        string path = match.Groups["path"].Value.Replace(new string('\\', 2), new string('\\', 1));
                        if (Directory.Exists(path))
                            libraryRoots.Add(path);
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            foreach (string libraryRoot in libraryRoots)
            {
                string steamAppsPath = Path.Combine(libraryRoot, "steamapps");
                IEnumerable<string> manifests;
                try { manifests = Directory.EnumerateFiles(steamAppsPath, "appmanifest_*.acf").ToArray(); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }

                foreach (string manifestPath in manifests)
                {
                    try
                    {
                        string manifest = File.ReadAllText(manifestPath);
                        if (!uint.TryParse(ReadVdfValue(manifest, "appid"), out uint appId))
                            continue;

                        string name = ReadVdfValue(manifest, "name") ?? $"Steam App {appId}";
                        string installDirectory = ReadVdfValue(manifest, "installdir") ?? "";
                        if (string.IsNullOrWhiteSpace(installDirectory))
                            continue;

                        string installPath = Path.GetFullPath(Path.Combine(steamAppsPath, "common", installDirectory));
                        if (Directory.Exists(installPath))
                            games.Add(new InstalledGame(appId, name, steamRoot, installPath));
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    catch (ArgumentException) { }
                }
            }

            games.AddRange(ReadNonSteamShortcuts(steamRoot));
        }

        return games.OrderByDescending(game => game.InstallPath.Length).ToArray();
    }

    private static IReadOnlyList<InstalledGame> ReadNonSteamShortcuts(string steamRoot)
    {
        var games = new List<InstalledGame>();
        string userDataPath = Path.Combine(steamRoot, "userdata");
        IEnumerable<string> userDirectories;
        try { userDirectories = Directory.EnumerateDirectories(userDataPath).ToArray(); }
        catch (IOException) { return games; }
        catch (UnauthorizedAccessException) { return games; }

        foreach (string userDirectory in userDirectories)
        {
            string shortcutsPath = Path.Combine(userDirectory, "config", "shortcuts.vdf");
            try
            {
                games.AddRange(ReadShortcutFile(steamRoot, shortcutsPath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or OverflowException)
            {
                // Ignore a malformed or temporarily locked per-user shortcuts file.
            }
        }

        return games;
    }

    private static IReadOnlyList<InstalledGame> ReadShortcutFile(string steamRoot, string shortcutsPath)
    {
        var games = new List<InstalledGame>();
        var file = new FileInfo(shortcutsPath);
        if (!file.Exists || file.Length < 3 || file.Length > MaxShortcutFileBytes)
            return games;

        using var stream = new FileStream(shortcutsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        if (reader.ReadByte() != 0 || !string.Equals(ReadNullTerminatedString(reader), "shortcuts", StringComparison.OrdinalIgnoreCase))
            return games;

        Dictionary<string, object?> shortcuts = ReadBinaryObject(reader, depth: 0);
        foreach (object? shortcutValue in shortcuts.Values)
        {
            if (shortcutValue is not Dictionary<string, object?> shortcut)
                continue;

            if (!TryReadAppId(shortcut, out uint appId))
                continue;
            string? name = GetString(shortcut, "AppName");
            string? executable = ExtractExecutablePath(GetString(shortcut, "Exe"));
            string? arguments = GetString(shortcut, "LaunchOptions");
            if (string.IsNullOrWhiteSpace(name) || executable is null)
                continue;

            games.Add(new InstalledGame(appId, name, steamRoot, "", executable, arguments, IsNonSteamShortcut: true));
        }

        return games;
    }

    private static Dictionary<string, object?> ReadBinaryObject(BinaryReader reader, int depth)
    {
        if (depth > 32)
            throw new InvalidDataException("A estrutura de atalhos da Steam é profunda demais.");

        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            byte type = reader.ReadByte();
            if (type == 8)
                return values;

            string key = ReadNullTerminatedString(reader);
            object? value = type switch
            {
                0 => ReadBinaryObject(reader, depth + 1),
                1 => ReadNullTerminatedString(reader),
                2 => reader.ReadInt32(),
                3 => reader.ReadSingle(),
                4 => ReadAndSkip(reader, 4),
                5 => ReadNullTerminatedWideString(reader),
                6 => ReadAndSkip(reader, 4),
                7 => reader.ReadUInt64(),
                _ => throw new InvalidDataException($"Tipo binário VDF não reconhecido: {type}.")
            };
            values[key] = value;
        }

        throw new EndOfStreamException("O arquivo de atalhos da Steam está incompleto.");
    }

    private static object? ReadAndSkip(BinaryReader reader, int length)
    {
        if (reader.BaseStream.Length - reader.BaseStream.Position < length)
            throw new EndOfStreamException();
        reader.BaseStream.Position += length;
        return null;
    }

    private static string ReadNullTerminatedString(BinaryReader reader)
    {
        using var bytes = new MemoryStream();
        while (reader.BaseStream.Position < reader.BaseStream.Length && bytes.Length < 64 * 1024)
        {
            byte value = reader.ReadByte();
            if (value == 0)
                return Encoding.UTF8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length);
            bytes.WriteByte(value);
        }

        throw new InvalidDataException("Uma string VDF não terminou corretamente.");
    }

    private static string ReadNullTerminatedWideString(BinaryReader reader)
    {
        var result = new StringBuilder();
        while (reader.BaseStream.Position + sizeof(ushort) <= reader.BaseStream.Length && result.Length < 32 * 1024)
        {
            ushort value = reader.ReadUInt16();
            if (value == 0)
                return result.ToString();
            result.Append((char)value);
        }

        throw new InvalidDataException("Uma string Unicode VDF não terminou corretamente.");
    }

    private static bool TryReadAppId(Dictionary<string, object?> shortcut, out uint appId)
    {
        if (shortcut.TryGetValue("appid", out object? value))
        {
            if (value is int signedAppId)
            {
                appId = unchecked((uint)signedAppId);
                return true;
            }
            if (value is uint unsignedAppId)
            {
                appId = unsignedAppId;
                return true;
            }
            if (value is ulong wideAppId && wideAppId <= uint.MaxValue)
            {
                appId = (uint)wideAppId;
                return true;
            }
        }

        appId = 0;
        return false;
    }

    private static string? GetString(Dictionary<string, object?> values, string key)
        => values.TryGetValue(key, out object? value) ? value as string : null;

    private static string? ExtractExecutablePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        string value = command.Trim();
        if (value.StartsWith('"'))
        {
            int closingQuote = value.IndexOf('"', 1);
            if (closingQuote > 1)
                value = value[1..closingQuote];
        }
        else
        {
            int firstSpace = value.IndexOf(' ');
            if (firstSpace > 0)
                value = value[..firstSpace];
        }

        value = UnescapeWindowsPath(value.Trim());
        value = Environment.ExpandEnvironmentVariables(value);
        try
        {
            string path = Path.GetFullPath(value);
            return path;
        }
        catch (ArgumentException) { return null; }
        catch (PathTooLongException) { return null; }
    }

    private static string UnescapeWindowsPath(string path)
    {
        if (path.StartsWith("\\\\", StringComparison.Ordinal))
            return "\\\\" + path[2..].Replace("\\\\", "\\", StringComparison.Ordinal);
        return path.Replace("\\\\", "\\", StringComparison.Ordinal);
    }

    private static IEnumerable<string> GetSteamRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static void AddRoot(HashSet<string> target, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;
            try
            {
                string path = Path.GetFullPath(value.Trim().Trim('"'));
                if (File.Exists(path) && string.Equals(Path.GetFileName(path), "steam.exe", StringComparison.OrdinalIgnoreCase))
                    path = Path.GetDirectoryName(path) ?? path;
                if (Directory.Exists(path))
                    target.Add(path);
            }
            catch (ArgumentException) { }
            catch (PathTooLongException) { }
        }

        try
        {
            using RegistryKey? userKey = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            AddRoot(roots, userKey?.GetValue("SteamPath") as string);
            AddRoot(roots, userKey?.GetValue("SteamExe") as string);
        }
        catch (SecurityException) { }
        catch (UnauthorizedAccessException) { }

        try
        {
            using RegistryKey? machineKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
            AddRoot(roots, machineKey?.GetValue("InstallPath") as string);
        }
        catch (SecurityException) { }
        catch (UnauthorizedAccessException) { }

        foreach (Process process in Process.GetProcessesByName("steam"))
        {
            try { AddRoot(roots, process.MainModule?.FileName); }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or UnauthorizedAccessException) { }
            finally { process.Dispose(); }
        }

        return roots;
    }

    private static SteamGameInfo ToShortcutInfo(InstalledGame game)
        => new(game.AppId, game.Name, game.SteamRoot, game.InstallPath,
            IsNonSteamShortcut: true, ShortcutArguments: game.ShortcutArguments);

    private static InstalledGame? FindShortcutByArguments(IReadOnlyList<InstalledGame> shortcuts, string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return null;

        string[] commandArguments = SplitWindowsCommandLine(commandLine);
        if (commandArguments.Length > 0)
            commandArguments = commandArguments[1..];

        ScoredShortcut[] matches = shortcuts
            .Select(game => new ScoredShortcut(game, GetArgumentMatchScore(commandArguments, game.ShortcutArguments)))
            .Where(match => match.ArgumentCount > 0)
            .OrderByDescending(match => match.ArgumentCount)
            .ToArray();
        if (matches.Length == 0 || (matches.Length > 1 && matches[0].ArgumentCount == matches[1].ArgumentCount))
            return null;

        return matches[0].Game;
    }

    private static int GetArgumentMatchScore(IReadOnlyList<string> commandArguments, string? launchOptions)
    {
        if (string.IsNullOrWhiteSpace(launchOptions))
            return 0;

        string[] expected = SplitWindowsCommandLine(launchOptions);
        if (expected.Length == 0 || expected.Length > commandArguments.Count)
            return 0;

        for (int start = 0; start <= commandArguments.Count - expected.Length; start++)
        {
            bool matched = true;
            for (int index = 0; index < expected.Length; index++)
            {
                if (!string.Equals(commandArguments[start + index], expected[index], StringComparison.OrdinalIgnoreCase))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
                return expected.Length;
        }

        return 0;
    }

    private static string[] SplitWindowsCommandLine(string commandLine)
    {
        IntPtr arguments = CommandLineToArgvW(commandLine, out int count);
        if (arguments == IntPtr.Zero || count <= 0)
            return Array.Empty<string>();

        try
        {
            var result = new string[count];
            for (int index = 0; index < count; index++)
            {
                IntPtr value = Marshal.ReadIntPtr(arguments, index * IntPtr.Size);
                result[index] = Marshal.PtrToStringUni(value) ?? "";
            }
            return result;
        }
        finally
        {
            _ = LocalFree(arguments);
        }
    }

    private static InstalledGame? FindShortcutByWindowTitle(IReadOnlyList<InstalledGame> shortcuts, string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

        InstalledGame[] matches = shortcuts
            .Where(game => title.Contains(game.Name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(game => game.Name.Length)
            .ToArray();
        if (matches.Length == 0 || (matches.Length > 1 && matches[0].Name.Length == matches[1].Name.Length))
            return null;

        return matches[0];
    }

    private static string GetWindowTitle(IntPtr window)
    {
        var title = new StringBuilder(512);
        _ = GetWindowText(window, title, title.Capacity);
        return title.ToString();
    }

    private static string? GetProcessCommandLine(uint processId)
    {
        using SafeFileHandle process = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, processId);
        if (process.IsInvalid || !IsWow64Process(process, out bool isWow64))
            return null;

        IntPtr processEnvironmentBlock;
        bool is32BitProcess;
        if (isWow64)
        {
            int status = NtQueryInformationProcessWow64(
                process, ProcessWow64InformationClass, out processEnvironmentBlock, IntPtr.Size, out _);
            if (status < 0 || processEnvironmentBlock == IntPtr.Zero)
                return null;
            is32BitProcess = true;
        }
        else
        {
            var information = new ProcessBasicInformation();
            int status = NtQueryInformationProcessBasic(
                process, ProcessBasicInformationClass, ref information,
                Marshal.SizeOf<ProcessBasicInformation>(), out _);
            if (status < 0 || information.PebBaseAddress == IntPtr.Zero)
                return null;
            processEnvironmentBlock = information.PebBaseAddress;
            is32BitProcess = IntPtr.Size == 4;
        }

        int processParametersOffset = is32BitProcess ? 0x10 : 0x20;
        int commandLineOffset = is32BitProcess ? 0x40 : 0x70;
        IntPtr processParameters = ReadRemotePointer(process, IntPtr.Add(processEnvironmentBlock, processParametersOffset), is32BitProcess);
        if (processParameters == IntPtr.Zero)
            return null;

        byte[] unicodeString = new byte[is32BitProcess ? 8 : 16];
        IntPtr unicodeStringAddress = IntPtr.Add(processParameters, commandLineOffset);
        if (!ReadRemoteMemory(process, unicodeStringAddress, unicodeString))
            return null;

        int byteLength = BitConverter.ToUInt16(unicodeString, 0);
        int bufferOffset = is32BitProcess ? 4 : 8;
        IntPtr commandLineBuffer = ReadPointer(unicodeString, bufferOffset, is32BitProcess);
        if (byteLength <= 0 || byteLength > 64 * 1024 || commandLineBuffer == IntPtr.Zero)
            return null;

        byte[] commandLineBytes = new byte[byteLength];
        return ReadRemoteMemory(process, commandLineBuffer, commandLineBytes)
            ? Encoding.Unicode.GetString(commandLineBytes)
            : null;
    }

    private static IntPtr ReadRemotePointer(SafeFileHandle process, IntPtr address, bool is32BitProcess)
    {
        byte[] bytes = new byte[is32BitProcess ? 4 : 8];
        return ReadRemoteMemory(process, address, bytes) ? ReadPointer(bytes, 0, is32BitProcess) : IntPtr.Zero;
    }

    private static IntPtr ReadPointer(byte[] bytes, int offset, bool is32BitProcess)
        => is32BitProcess
            ? new IntPtr((long)BitConverter.ToUInt32(bytes, offset))
            : new IntPtr(BitConverter.ToInt64(bytes, offset));

    private static bool ReadRemoteMemory(SafeFileHandle process, IntPtr address, byte[] buffer)
        => ReadProcessMemory(process, address, buffer, new IntPtr(buffer.Length), out IntPtr bytesRead)
            && bytesRead.ToInt64() == buffer.Length;

    private static IReadOnlyList<ProcessPathInfo> GetProcessTree(uint processId)
    {
        Dictionary<uint, uint> parents = ReadProcessParents();
        var processes = new List<ProcessPathInfo>();
        var visited = new HashSet<uint>();
        uint current = processId;

        for (int depth = 0; depth < 16 && current != 0 && visited.Add(current); depth++)
        {
            string? path = GetProcessExecutablePath(current);
            if (path is not null)
                processes.Add(new ProcessPathInfo(current, path));

            if (!parents.TryGetValue(current, out current))
                break;
        }

        return processes;
    }

    private static string? GetProcessExecutablePath(uint processId)
    {
        try
        {
            using SafeFileHandle processHandle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (!processHandle.IsInvalid)
            {
                var path = new StringBuilder(32768);
                uint pathLength = (uint)path.Capacity;
                if (QueryFullProcessImageName(processHandle, 0, path, ref pathLength) && pathLength > 0)
                    return path.ToString();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
        }

        try
        {
            using Process process = Process.GetProcessById((int)processId);
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static Dictionary<uint, uint> ReadProcessParents()
    {
        var parents = new Dictionary<uint, uint>();
        using SafeFileHandle snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (snapshot.IsInvalid)
            return parents;

        var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
        if (!Process32First(snapshot, ref entry))
            return parents;

        do
        {
            parents[entry.th32ProcessID] = entry.th32ParentProcessID;
            entry.dwSize = (uint)Marshal.SizeOf<ProcessEntry32>();
        } while (Process32Next(snapshot, ref entry));

        return parents;
    }

    private static string? ReadVdfValue(string contents, string key)
    {
        string pattern = "\\\"" + Regex.Escape(key) + "\\\"\\s+\\\"(?<value>[^\\\"]*)\\\"";
        Match match = Regex.Match(contents, pattern, RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static bool IsSameOrChildPath(string candidate, string root)
    {
        try
        {
            string fullCandidate = Path.GetFullPath(candidate);
            string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            return fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || string.Equals(fullCandidate, fullRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
        catch (PathTooLongException) { return false; }
    }

    private static bool PathsEqual(string first, string second)
    {
        try
        {
            return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
        catch (PathTooLongException) { return false; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public UIntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string? szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        SafeFileHandle process, uint flags, StringBuilder executableName, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process(SafeFileHandle process, [MarshalAs(UnmanagedType.Bool)] out bool wow64Process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(SafeFileHandle process, IntPtr baseAddress, byte[] buffer, IntPtr size, out IntPtr bytesRead);

    [DllImport("ntdll.dll", EntryPoint = "NtQueryInformationProcess")]
    private static extern int NtQueryInformationProcessBasic(
        SafeFileHandle process, int informationClass, ref ProcessBasicInformation information, int informationLength, out int returnLength);

    [DllImport("ntdll.dll", EntryPoint = "NtQueryInformationProcess")]
    private static extern int NtQueryInformationProcessWow64(
        SafeFileHandle process, int informationClass, out IntPtr information, int informationLength, out int returnLength);

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry32 entry);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximumCount);
}

internal static class SteamCoverProvider
{
    private const long MaxDownloadBytes = 20 * 1024 * 1024;
    private const long MaxAppInfoFileBytes = 128 * 1024 * 1024;
    private const uint CryptProtectUiForbidden = 0x1;
    private const uint AppInfoV40Magic = 0x07564428;
    private const uint AppInfoV41Magic = 0x07564429;
    private static readonly HttpClient Client = CreateClient();
    private static readonly string[] SteamStaticHosts = ["shared.fastly.steamstatic.com", "shared.steamstatic.com"];
    private static readonly HashSet<string> AppInfoAssetFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "library_capsule.jpg",
        "library_capsule_2x.jpg",
        "library_header.jpg",
        "library_header_2x.jpg"
    };
    private sealed record CachedAppInfoAssets(long FileLength, long LastWriteUtcTicks, IReadOnlyDictionary<string, string> Paths);
    private static readonly object AppInfoAssetsCacheGate = new();
    private static readonly Dictionary<string, CachedAppInfoAssets> AppInfoAssetsCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string SteamGridDbKeyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JungleLeopardStudio", "steamgriddb.key");
    private static readonly string SteamGridDbCachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JungleLeopardStudio", "steamgriddb-cache");
    private static readonly object SteamGridDbMissCacheGate = new();
    private static readonly Dictionary<(uint AppId, bool Portrait), DateTime> SteamGridDbMissCache = new();

    internal sealed record CoverResult(byte[] Bytes, string Source);

    public static bool HasSteamGridDbApiKey
    {
        get
        {
            try { return !string.IsNullOrWhiteSpace(ReadSteamGridDbApiKey()); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or CryptographicException or FormatException) { return false; }
        }
    }

    public static void SaveSteamGridDbApiKey(string apiKey)
    {
        string normalized = apiKey.Trim();
        if (normalized.Length is < 8 or > 4096 || normalized.Any(char.IsControl))
            throw new ArgumentException("A chave API parece inválida. Confira o valor e tente novamente.", nameof(apiKey));

        byte[] plainBytes = Encoding.UTF8.GetBytes(normalized);
        byte[] protectedBytes;
        try { protectedBytes = ProtectForCurrentUser(plainBytes); }
        finally { CryptographicOperations.ZeroMemory(plainBytes); }

        Directory.CreateDirectory(Path.GetDirectoryName(SteamGridDbKeyPath)!);
        string temporaryPath = SteamGridDbKeyPath + ".tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, protectedBytes);
            File.Move(temporaryPath, SteamGridDbKeyPath, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        lock (SteamGridDbMissCacheGate) SteamGridDbMissCache.Clear();
    }

    public static void RemoveSteamGridDbApiKey()
    {
        try { if (File.Exists(SteamGridDbKeyPath)) File.Delete(SteamGridDbKeyPath); }
        finally { lock (SteamGridDbMissCacheGate) SteamGridDbMissCache.Clear(); }
    }

    public static async Task<CoverResult> GetCoverAsync(SteamGameInfo game, bool preferPortrait, CancellationToken cancellationToken)
    {
        CoverResult? communityCover = null;
        bool communityLookupAttempted = false;
        string? cachedPath = FindCachedCover(game, preferPortrait);
        if (cachedPath is not null)
            return new CoverResult(await File.ReadAllBytesAsync(cachedPath, cancellationToken).ConfigureAwait(false), "cache local da Steam");

        if (!game.IsNonSteamShortcut)
        {
            string[] requestedAssets = preferPortrait
                ? ["library_capsule_2x.jpg", "library_capsule.jpg"]
                : ["library_header_2x.jpg", "library_header.jpg"];
            byte[]? requestedCover = await TrySourceAsync(
                () => TryDownloadAppInfoAssetAsync(game, requestedAssets, cancellationToken), cancellationToken).ConfigureAwait(false);
            if (requestedCover is not null)
                return new CoverResult(requestedCover, "arte oficial da Steam");

            string[] legacyAssetNames = preferPortrait
                ? ["library_600x900_2x.jpg", "library_600x900.jpg", "library_capsule_2x.jpg", "library_capsule.jpg"]
                : ["library_header_2x.jpg", "library_header.jpg"];
            byte[]? legacyCover = await TrySourceAsync(
                () => TryDownloadLegacyAssetsAsync(game, legacyAssetNames, cancellationToken), cancellationToken).ConfigureAwait(false);
            if (legacyCover is not null)
                return new CoverResult(legacyCover, "CDN da Steam");

            // Prefer SteamGridDB art in the panel's requested orientation before falling back to a mismatched Steam image.
            communityLookupAttempted = true;
            communityCover = await TryGetSteamGridDbCoverAsync(game, preferPortrait, cancellationToken).ConfigureAwait(false);
            if (communityCover is not null)
                return communityCover;

            if (preferPortrait)
            {
                string? horizontalCachedPath = FindCachedCover(game, preferPortrait: false);
                if (horizontalCachedPath is not null)
                    return new CoverResult(await File.ReadAllBytesAsync(horizontalCachedPath, cancellationToken).ConfigureAwait(false), "cache local da Steam (imagem horizontal)");

                byte[]? horizontalCover = await TrySourceAsync(
                    () => TryDownloadAppInfoAssetAsync(game, ["library_header_2x.jpg", "library_header.jpg"], cancellationToken), cancellationToken).ConfigureAwait(false);
                if (horizontalCover is null)
                    horizontalCover = await TrySourceAsync(
                        () => TryDownloadLegacyAssetsAsync(game, ["library_header_2x.jpg", "library_header.jpg"], cancellationToken), cancellationToken).ConfigureAwait(false);
                if (horizontalCover is not null)
                    return new CoverResult(horizontalCover, "arte horizontal da Steam");
            }

            byte[]? storeHeader = await TrySourceAsync(() => DownloadStoreHeaderImageAsync(game, cancellationToken), cancellationToken).ConfigureAwait(false);
            if (storeHeader is not null)
                return new CoverResult(storeHeader, "página da loja Steam");
        }

        if (!communityLookupAttempted)
            communityCover = await TryGetSteamGridDbCoverAsync(game, preferPortrait, cancellationToken).ConfigureAwait(false);
        if (communityCover is not null)
            return communityCover;

        if (!HasSteamGridDbApiKey)
            throw new InvalidOperationException("A Steam não forneceu uma imagem de capa acessível. Configure uma chave API do SteamGridDB na seção Integrações para habilitar o fallback.");
        string direction = preferPortrait ? "vertical" : "horizontal";
        throw new InvalidOperationException($"A Steam e o SteamGridDB não disponibilizaram uma capa {direction} para este jogo.");
    }

    private static async Task<byte[]?> TrySourceAsync(Func<Task<byte[]?>> source, CancellationToken cancellationToken)
    {
        try { return await source().ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException or UnauthorizedAccessException or SecurityException or InvalidDataException)
        {
            return null;
        }
    }

    private static async Task<CoverResult?> TryGetSteamGridDbCoverAsync(
        SteamGameInfo game,
        bool preferPortrait,
        CancellationToken cancellationToken)
    {
        string cachedCommunityPath = Path.Combine(SteamGridDbCachePath, $"{game.AppId}_{(preferPortrait ? "portrait" : "landscape")}.img");
        if (File.Exists(cachedCommunityPath))
        {
            try { return new CoverResult(await File.ReadAllBytesAsync(cachedCommunityPath, cancellationToken).ConfigureAwait(false), "cache do SteamGridDB"); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        var missKey = (game.AppId, preferPortrait);
        lock (SteamGridDbMissCacheGate)
            if (SteamGridDbMissCache.TryGetValue(missKey, out DateTime expiry) && expiry > DateTime.UtcNow)
                return null;

        string? apiKey;
        try { apiKey = ReadSteamGridDbApiKey(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or CryptographicException or FormatException)
        { return null; }
        if (string.IsNullOrWhiteSpace(apiKey))
            return null;

        string[] types = preferPortrait ? ["grids", "heroes"] : ["heroes", "grids"];
        foreach (string type in types)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string dimensions = type == "grids"
                ? (preferPortrait ? "600x900,660x930,342x482" : "920x430,460x215,600x900")
                : "1920x620,3840x1240,1600x650";
            IReadOnlyList<string> assetUrls = await FindSteamGridDbAssetUrlsAsync(type, game, dimensions, apiKey, cancellationToken).ConfigureAwait(false);
            foreach (string assetUrl in assetUrls)
            {
                try
                {
                    byte[] bytes = await DownloadImageAsync(assetUrl, cancellationToken).ConfigureAwait(false);
                    if (!LooksLikeSupportedImage(bytes))
                        continue;
                    Directory.CreateDirectory(SteamGridDbCachePath);
                    await File.WriteAllBytesAsync(cachedCommunityPath, bytes, cancellationToken).ConfigureAwait(false);
                    lock (SteamGridDbMissCacheGate) SteamGridDbMissCache.Remove(missKey);
                    return new CoverResult(bytes, "SteamGridDB");
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                catch (IOException) { }
            }
        }

        lock (SteamGridDbMissCacheGate) SteamGridDbMissCache[missKey] = DateTime.UtcNow.AddMinutes(10);
        return null;
    }

    private static async Task<IReadOnlyList<string>> FindSteamGridDbAssetUrlsAsync(string type, SteamGameInfo game, string dimensions, string apiKey, CancellationToken cancellationToken)
    {
        string collectionEndpoint = type == "grids" ? "grids" : "heroes";
        if (!game.IsNonSteamShortcut)
        {
            IReadOnlyList<string> urls = await QuerySteamGridDbAssetUrlsAsync(
                $"{collectionEndpoint}/steam/{game.AppId}?dimensions={Uri.EscapeDataString(dimensions)}&mimes=image%2Fpng%2Cimage%2Fjpeg&types=static&nsfw=false&humor=false&epilepsy=false&limit=5",
                apiKey, cancellationToken).ConfigureAwait(false);
            if (urls.Count > 0)
                return urls;
        }

        string? gameId = await FindSteamGridDbGameIdByNameAsync(game.Name, apiKey, cancellationToken).ConfigureAwait(false);
        return gameId is null ? Array.Empty<string>() : await QuerySteamGridDbAssetUrlsAsync(
            $"{collectionEndpoint}/game/{gameId}?dimensions={Uri.EscapeDataString(dimensions)}&mimes=image%2Fpng%2Cimage%2Fjpeg&types=static&nsfw=false&humor=false&epilepsy=false&limit=5",
            apiKey, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> FindSteamGridDbGameIdByNameAsync(string name, string apiKey, CancellationToken cancellationToken)
    {
        try
        {
            using JsonDocument response = await GetSteamGridDbJsonAsync($"search/autocomplete/{Uri.EscapeDataString(name)}", apiKey, cancellationToken).ConfigureAwait(false);
            if (!response.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
                return null;

            string normalizedName = NormalizeGameTitle(name);
            foreach (JsonElement result in data.EnumerateArray())
            {
                JsonElement candidate = result;
                if (result.TryGetProperty("data", out JsonElement nested) && nested.ValueKind == JsonValueKind.Object)
                    candidate = nested;
                if (!candidate.TryGetProperty("name", out JsonElement title)
                    || !string.Equals(NormalizeGameTitle(title.GetString() ?? ""), normalizedName, StringComparison.Ordinal))
                    continue;
                if (candidate.TryGetProperty("id", out JsonElement id) && id.TryGetInt32(out int value) && value > 0)
                    return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        { throw new InvalidOperationException("O SteamGridDB rejeitou a chave API configurada. Confira a chave em Integrações > SteamGridDB.", ex); }
        catch (HttpRequestException) { }
        catch (JsonException) { }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        return null;
    }

    private static string NormalizeGameTitle(string title)
        => Regex.Replace(title.Normalize(NormalizationForm.FormKC), "[^\\p{L}\\p{N}]", "", RegexOptions.CultureInvariant).ToUpperInvariant();

    private static async Task<IReadOnlyList<string>> QuerySteamGridDbAssetUrlsAsync(string endpoint, string apiKey, CancellationToken cancellationToken)
    {
        try
        {
            using JsonDocument response = await GetSteamGridDbJsonAsync(endpoint, apiKey, cancellationToken).ConfigureAwait(false);
            if (!response.RootElement.TryGetProperty("success", out JsonElement success)
                || success.ValueKind != JsonValueKind.True
                || !response.RootElement.TryGetProperty("data", out JsonElement data)
                || data.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();

            var urls = new List<string>();
            foreach (JsonElement asset in data.EnumerateArray())
                if (asset.TryGetProperty("url", out JsonElement value)
                    && Uri.TryCreate(value.GetString(), UriKind.Absolute, out Uri? url)
                    && url.Scheme == Uri.UriSchemeHttps)
                    urls.Add(url.AbsoluteUri);
            return urls;
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        { throw new InvalidOperationException("O SteamGridDB rejeitou a chave API configurada. Confira a chave em Integrações > SteamGridDB.", ex); }
        catch (HttpRequestException) { }
        catch (JsonException) { }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        return Array.Empty<string>();
    }

    private static bool LooksLikeSupportedImage(ReadOnlySpan<byte> bytes)
        => bytes.Length >= 8
            && (bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })
                || (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF));

    private static async Task<JsonDocument> GetSteamGridDbJsonAsync(string endpoint, string apiKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.steamgriddb.com/api/v2/" + endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using HttpResponseMessage response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static string? ReadSteamGridDbApiKey()
    {
        if (!File.Exists(SteamGridDbKeyPath))
            return null;
        FileInfo keyFile = new(SteamGridDbKeyPath);
        if (keyFile.Length is <= 0 or > 64 * 1024)
            return null;
        byte[] encrypted = File.ReadAllBytes(SteamGridDbKeyPath);
        byte[] plain;
        try { plain = TransformWithDpapi(encrypted, protect: false); }
        finally { CryptographicOperations.ZeroMemory(encrypted); }
        try { return Encoding.UTF8.GetString(plain).Trim(); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static byte[] ProtectForCurrentUser(byte[] input) => TransformWithDpapi(input, protect: true);

    private static byte[] TransformWithDpapi(byte[] inputBytes, bool protect)
    {
        GCHandle inputHandle = GCHandle.Alloc(inputBytes, GCHandleType.Pinned);
        var input = new DataBlob { Size = inputBytes.Length, Data = inputHandle.AddrOfPinnedObject() };
        DataBlob output = default;
        IntPtr description = IntPtr.Zero;
        bool succeeded;
        try
        {
            succeeded = protect
                ? CryptProtectData(ref input, "SteamGridDB API key", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output)
                : CryptUnprotectData(ref input, out description, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output);
        }
        finally { inputHandle.Free(); }

        try
        {
            if (!succeeded)
                throw new CryptographicException(Marshal.GetLastWin32Error());
            byte[] result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            if (output.Data != IntPtr.Zero) _ = LocalFree(output.Data);
            if (description != IntPtr.Zero) _ = LocalFree(description);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Size; public IntPtr Data; }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string description, IntPtr optionalEntropy, IntPtr reserved,
        IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, out IntPtr description, IntPtr optionalEntropy, IntPtr reserved,
        IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    private static async Task<byte[]?> DownloadStoreHeaderImageAsync(SteamGameInfo game, CancellationToken cancellationToken)
    {
        string detailsUrl = $"https://store.steampowered.com/api/appdetails?appids={game.AppId}&l=en";
        using HttpResponseMessage detailsResponse = await Client.GetAsync(detailsUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        detailsResponse.EnsureSuccessStatusCode();
        await using Stream detailsStream = await detailsResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument details = await JsonDocument.ParseAsync(detailsStream, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!details.RootElement.TryGetProperty(game.AppId.ToString(), out JsonElement app)
            || !app.TryGetProperty("success", out JsonElement success)
            || !success.GetBoolean()
            || !app.TryGetProperty("data", out JsonElement data)
            || !data.TryGetProperty("header_image", out JsonElement headerImage)
            || string.IsNullOrWhiteSpace(headerImage.GetString()))
            return null;

        return await TryDownloadImageAsync(headerImage.GetString()!, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]?> TryDownloadAppInfoAssetAsync(
        SteamGameInfo game,
        IReadOnlyList<string> assetFileNames,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, string> assets = await Task.Run(
            () => GetAppInfoAssetPaths(game), cancellationToken).ConfigureAwait(false);
        foreach (string assetFileName in assetFileNames)
        {
            if (!assets.TryGetValue(assetFileName, out string? relativePath))
                continue;

            HttpRequestException? lastRequestError = null;
            foreach (string host in SteamStaticHosts)
            {
                string assetUrl = $"https://{host}/store_item_assets/steam/apps/{game.AppId}/{relativePath}";
                try
                {
                    byte[]? image = await TryDownloadImageAsync(assetUrl, cancellationToken).ConfigureAwait(false);
                    if (image is not null)
                        return image;
                }
                catch (HttpRequestException ex)
                {
                    lastRequestError = ex;
                }
            }

            if (lastRequestError is not null)
                throw new HttpRequestException($"NÃ£o foi possÃ­vel baixar a arte Steam {assetFileName}.", lastRequestError);
        }

        return null;
    }

    private static async Task<byte[]?> TryDownloadLegacyAssetsAsync(
        SteamGameInfo game,
        IReadOnlyList<string> assetFileNames,
        CancellationToken cancellationToken)
    {
        foreach (string assetFileName in assetFileNames)
        {
            string assetUrl = $"https://shared.steamstatic.com/store_item_assets/steam/apps/{game.AppId}/{assetFileName}";
            byte[]? image = await TryDownloadImageAsync(assetUrl, cancellationToken).ConfigureAwait(false);
            if (image is not null)
                return image;
        }

        return null;
    }

    private static IReadOnlyDictionary<string, string> GetAppInfoAssetPaths(SteamGameInfo game)
    {
        string appInfoPath = Path.Combine(game.SteamRoot, "appcache", "appinfo.vdf");
        try
        {
            var appInfoFile = new FileInfo(appInfoPath);
            if (!appInfoFile.Exists || appInfoFile.Length < 8 || appInfoFile.Length > MaxAppInfoFileBytes)
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            string cacheKey = $"{appInfoFile.FullName}|{game.AppId}";
            long lastWriteTicks = appInfoFile.LastWriteTimeUtc.Ticks;
            lock (AppInfoAssetsCacheGate)
            {
                if (AppInfoAssetsCache.TryGetValue(cacheKey, out CachedAppInfoAssets? cached)
                    && cached.FileLength == appInfoFile.Length
                    && cached.LastWriteUtcTicks == lastWriteTicks)
                    return cached.Paths;
            }

            Dictionary<string, string> paths = ReadAppInfoAssetPaths(appInfoFile.FullName, game.AppId);
            lock (AppInfoAssetsCacheGate)
                AppInfoAssetsCache[cacheKey] = new CachedAppInfoAssets(appInfoFile.Length, lastWriteTicks, paths);
            return paths;
        }
        catch (IOException) { return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }
        catch (UnauthorizedAccessException) { return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }
        catch (SecurityException) { return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }
        catch (InvalidDataException) { return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }
        catch (ArgumentException) { return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }
        catch (OverflowException) { return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }
    }

    private static Dictionary<string, string> ReadAppInfoAssetPaths(string appInfoPath, uint targetAppId)
    {
        byte[] data = File.ReadAllBytes(appInfoPath);
        using var stream = new MemoryStream(data, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        uint magic = reader.ReadUInt32();
        bool usesStringTable = magic == AppInfoV41Magic;
        if (!usesStringTable && magic != AppInfoV40Magic)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        _ = reader.ReadUInt32(); // Steam universe
        ulong stringTableOffset = usesStringTable ? reader.ReadUInt64() : 0;
        string[]? stringTable = usesStringTable ? ReadAppInfoStringTable(data, stringTableOffset) : null;
        long entriesEnd = usesStringTable ? checked((long)stringTableOffset) : stream.Length;
        stream.Position = usesStringTable ? 16 : 8;

        while (stream.Position + sizeof(uint) <= entriesEnd)
        {
            uint appId = reader.ReadUInt32();
            if (appId == 0 || stream.Position + sizeof(uint) > entriesEnd)
                break;

            uint entrySize = reader.ReadUInt32();
            long entryEnd = checked(stream.Position + entrySize);
            if (entrySize < 60 || entryEnd > entriesEnd)
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (appId == targetAppId)
            {
                stream.Position += 60; // Fixed entry metadata before the binary VDF payload.
                int payloadLength = checked((int)(entryEnd - stream.Position));
                using var payloadStream = new MemoryStream(data, checked((int)stream.Position), payloadLength, writable: false);
                using var payloadReader = new BinaryReader(payloadStream, Encoding.UTF8, leaveOpen: true);
                Dictionary<string, object?> appInfo = ReadAppInfoObject(payloadReader, stringTable, usesStringTable, depth: 0);
                var assets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                CollectAppInfoAssetPaths(appInfo, assets);
                return assets;
            }

            stream.Position = entryEnd;
        }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static string[] ReadAppInfoStringTable(byte[] data, ulong offset)
    {
        if (offset > (ulong)data.Length || (ulong)data.Length - offset < sizeof(uint) || offset > int.MaxValue)
            throw new InvalidDataException("A tabela de strings do appinfo.vdf estÃ¡ fora do arquivo.");

        using var stream = new MemoryStream(data, (int)offset, data.Length - (int)offset, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        uint count = reader.ReadUInt32();
        if (count > 100_000)
            throw new InvalidDataException("A tabela de strings do appinfo.vdf excede o limite esperado.");

        var strings = new string[checked((int)count)];
        for (int index = 0; index < strings.Length; index++)
            strings[index] = ReadAppInfoNullTerminatedString(reader);
        return strings;
    }

    private static Dictionary<string, object?> ReadAppInfoObject(
        BinaryReader reader,
        string[]? stringTable,
        bool usesStringTable,
        int depth)
    {
        if (depth > 64)
            throw new InvalidDataException("A estrutura do appinfo.vdf Ã© profunda demais.");

        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            byte type = reader.ReadByte();
            if (type == 8)
                return values;

            string key;
            if (usesStringTable)
            {
                uint keyIndex = reader.ReadUInt32();
                if (stringTable is null || keyIndex >= (uint)stringTable.Length)
                    throw new InvalidDataException("O appinfo.vdf referencia uma chave invÃ¡lida.");
                key = stringTable[checked((int)keyIndex)];
            }
            else
            {
                key = ReadAppInfoNullTerminatedString(reader);
            }

            object? value = type switch
            {
                0 => ReadAppInfoObject(reader, stringTable, usesStringTable, depth + 1),
                1 => ReadAppInfoNullTerminatedString(reader),
                2 => reader.ReadInt32(),
                3 => reader.ReadSingle(),
                4 => reader.ReadUInt32(),
                5 => ReadAppInfoNullTerminatedWideString(reader),
                6 => reader.ReadUInt32(),
                7 => reader.ReadUInt64(),
                10 => reader.ReadInt64(),
                _ => throw new InvalidDataException($"Tipo binÃ¡rio VDF nÃ£o reconhecido: {type}.")
            };
            values[key] = value;
        }

        throw new EndOfStreamException("O appinfo.vdf terminou antes do fechamento de um objeto.");
    }

    private static string ReadAppInfoNullTerminatedString(BinaryReader reader)
    {
        using var bytes = new MemoryStream();
        while (reader.BaseStream.Position < reader.BaseStream.Length && bytes.Length < 64 * 1024)
        {
            byte value = reader.ReadByte();
            if (value == 0)
                return Encoding.UTF8.GetString(bytes.GetBuffer(), 0, checked((int)bytes.Length));
            bytes.WriteByte(value);
        }

        throw new InvalidDataException("Uma string do appinfo.vdf nÃ£o terminou corretamente.");
    }

    private static string ReadAppInfoNullTerminatedWideString(BinaryReader reader)
    {
        var result = new StringBuilder();
        while (reader.BaseStream.Position + sizeof(ushort) <= reader.BaseStream.Length && result.Length < 32 * 1024)
        {
            ushort value = reader.ReadUInt16();
            if (value == 0)
                return result.ToString();
            result.Append((char)value);
        }

        throw new InvalidDataException("Uma string Unicode do appinfo.vdf nÃ£o terminou corretamente.");
    }

    private static void CollectAppInfoAssetPaths(object? value, Dictionary<string, string> assets)
    {
        if (value is string text)
        {
            Match match = Regex.Match(text, @"^(?<hash>[a-f0-9]{40})/(?<file>[^/]+)$", RegexOptions.IgnoreCase);
            if (match.Success && AppInfoAssetFileNames.Contains(match.Groups["file"].Value))
                assets.TryAdd(match.Groups["file"].Value, text);
            return;
        }

        if (value is Dictionary<string, object?> dictionary)
        {
            foreach (object? child in dictionary.Values)
                CollectAppInfoAssetPaths(child, assets);
        }
    }

    private static string? FindCachedCover(SteamGameInfo game, bool preferPortrait)
    {
        string id = game.AppId.ToString();
        string[] extensions = [".jpg", ".png", ".webp", ".jpeg"];
        string[] baseNames = preferPortrait
            ? [$"{id}p", $"{id}_library_600x900_2x", $"{id}_library_600x900", $"{id}_library_capsule_2x", $"{id}_library_capsule"]
            : [$"{id}_library_header_2x", $"{id}_library_header", $"{id}_header", id, $"{id}_hero", $"{id}_library_hero"];
        string[] candidates = baseNames.SelectMany(name => extensions.Select(extension => name + extension)).ToArray();

        var directories = new List<string>();
        string userDataPath = Path.Combine(game.SteamRoot, "userdata");
        try
        {
            foreach (string userDirectory in Directory.EnumerateDirectories(userDataPath))
            {
                string gridDirectory = Path.Combine(userDirectory, "config", "grid");
                if (Directory.Exists(gridDirectory))
                    directories.Add(gridDirectory);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        directories.Add(Path.Combine(game.SteamRoot, "appcache", "librarycache"));
        directories.Add(Path.Combine(game.SteamRoot, "steamapps", "librarycache"));
        foreach (string directory in directories)
        {
            foreach (string candidate in candidates)
            {
                string path = Path.Combine(directory, candidate);
                if (File.Exists(path))
                    return path;
            }
        }

        return null;
    }

    private static async Task<byte[]?> TryDownloadImageAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            return await DownloadImageAsync(url, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            return null;
        }
    }

    private static async Task<byte[]> DownloadImageAsync(string url, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length > MaxDownloadBytes)
            throw new InvalidOperationException("A imagem retornada pela Steam excede o limite de download.");

        await using Stream imageStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[32 * 1024];
        while (true)
        {
            int count = await imageStream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (count == 0)
                break;
            if (buffer.Length + count > MaxDownloadBytes)
                throw new InvalidOperationException("A imagem retornada pela Steam excede o limite de download.");
            buffer.Write(chunk, 0, count);
        }

        return buffer.ToArray();
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("JungleLeopardScreenControl/1.0");
        return client;
    }
}
