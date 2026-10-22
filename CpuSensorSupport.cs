using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32;

namespace JungleLeopardPoc;

internal readonly record struct CpuSensorSetupResult(
    bool Succeeded,
    bool RebootRequired,
    int MatchingStartupTasks,
    int ElevatedStartupTasks,
    string? Error);

internal static class CpuSensorSupport
{
    private const string SetupResourceName = "JungleLeopardPoc.ThirdParty.PawnIO_setup.exe";
    private const string SetupSha256 = "1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032";
    private const int TaskEnumHidden = 1;
    private const int TaskCreateOrUpdate = 6;
    private const int TaskRunLevelHighest = 1;

    internal static bool IsPawnIoInstalled
    {
        get
        {
            // LibreHardwareMonitor.PawnIo.PawnIo.Version is initialized once per
            // process. The setup helper checks this before launching PawnIO's
            // installer, so that cached value remains null even after a
            // successful install. Read the uninstall registration afresh here.
            try { return TryGetInstalledPawnIoVersion() is not null; }
            catch (Exception ex) when (IsRecoverable(ex)) { return false; }
        }
    }

    private static Version? TryGetInstalledPawnIoVersion()
    {
        const string uninstallKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using RegistryKey? key = baseKey.OpenSubKey(uninstallKeyPath, writable: false);
            if (Version.TryParse(key?.GetValue("DisplayVersion") as string, out Version? version))
                return version;
        }

        return null;
    }

    internal static bool IsCurrentProcessElevated
    {
        get
        {
            try
            {
                using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception ex) when (IsRecoverable(ex)) { return false; }
        }
    }

    internal static string GetStatusText()
    {
        if (!IsPawnIoInstalled)
            return AppLanguage.Translate("O driver PawnIO, necessário para sensores que exigem acesso de baixo nível ao hardware, não está instalado. Use o botão para configurar o suporte a sensores.");
        if (!IsCurrentProcessElevated)
            return AppLanguage.Translate("Driver instalado, mas o app não está elevado. Use o botão para ajustar a tarefa de logon e permitir o acesso aos sensores.");
        return AppLanguage.Translate("Driver instalado e app elevado. O LibreHardwareMonitor integrado está lendo os sensores disponíveis.");
    }

    internal static bool StartSetupProcess(out string? error)
    {
        error = null;
        try
        {
            string executable = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName
                ?? throw new IOException("Não foi possível localizar o executável do aplicativo.");
            var start = new ProcessStartInfo(executable)
            {
                Arguments = $"--setup-cpu-sensor {Environment.ProcessId}",
                UseShellExecute = !IsCurrentProcessElevated,
                Verb = IsCurrentProcessElevated ? "" : "runas",
                WorkingDirectory = AppContext.BaseDirectory
            };
            Process.Start(start);
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            error = AppLanguage.Translate("A solicitação de administrador foi cancelada.");
            return false;
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            error = AppLanguage.Translate("Não foi possível iniciar a configuração dos sensores de hardware: ") + ex.Message;
            return false;
        }
    }

    internal static CpuSensorSetupResult InstallAndConfigure(int parentProcessId)
    {
        try
        {
            if (parentProcessId > 0 && parentProcessId != Environment.ProcessId)
            {
                try
                {
                    using Process parent = Process.GetProcessById(parentProcessId);
                    if (!parent.WaitForExit(milliseconds: 300_000))
                        return new(false, false, 0, 0, "A instância anterior do aplicativo não encerrou no tempo esperado.");
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }

            bool rebootRequired = false;
            if (!IsPawnIoInstalled)
            {
                string installerPath = ExtractVerifiedInstaller();
                try
                {
                    var start = new ProcessStartInfo(installerPath, "-install")
                    {
                        UseShellExecute = false,
                        WorkingDirectory = Path.GetDirectoryName(installerPath) ?? Path.GetTempPath()
                    };
                    using Process installer = Process.Start(start)
                        ?? throw new IOException("O instalador PawnIO não iniciou.");
                    installer.WaitForExit();
                    rebootRequired = installer.ExitCode == 3010;
                    if (installer.ExitCode != 0 && !rebootRequired)
                        return new(false, false, 0, 0, $"O instalador PawnIO terminou com o código {installer.ExitCode}.");
                    if (!IsPawnIoInstalled)
                        return new(false, rebootRequired, 0, 0, "O instalador terminou, mas o Windows ainda não registra PawnIO como instalado.");
                }
                finally
                {
                    try { File.Delete(installerPath); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }

            (int matchingTasks, int elevatedTasks, string? taskError) = SetMatchingStartupTasksToHighest();
            WriteDiagnostic($"PawnIO instalado/verificado. Tarefas de logon correspondentes: {matchingTasks}; elevadas: {elevatedTasks}; reinicialização necessária: {rebootRequired}; erro de tarefa: {taskError ?? "nenhum"}.");
            return new(true, rebootRequired, matchingTasks, elevatedTasks, taskError);
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            WriteDiagnostic($"Configuração do sensor da CPU falhou ({ex.GetType().Name}): {ex.Message}");
            return new(false, false, 0, 0, ex.Message);
        }
    }

    private static string ExtractVerifiedInstaller()
    {
        using Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(SetupResourceName)
            ?? throw new FileNotFoundException("O instalador PawnIO não está incluído nesta versão do aplicativo.");
        using var memory = new MemoryStream();
        resource.CopyTo(memory);
        byte[] bytes = memory.ToArray();
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!hash.Equals(SetupSha256, StringComparison.Ordinal))
            throw new InvalidDataException("A verificação SHA-256 do instalador PawnIO falhou.");

        string path = Path.Combine(Path.GetTempPath(), $"JungleLeopard-PawnIO-{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static (int Matching, int Elevated, string? Error) SetMatchingStartupTasksToHighest()
    {
        object? serviceObject = null;
        object? rootObject = null;
        int matching = 0;
        int elevated = 0;
        try
        {
            Type? serviceType = Type.GetTypeFromProgID("Schedule.Service", throwOnError: false);
            if (serviceType is null)
                return (0, 0, "O serviço Agendador de Tarefas não está disponível.");
            serviceObject = Activator.CreateInstance(serviceType);
            if (serviceObject is null)
                return (0, 0, "Não foi possível conectar ao Agendador de Tarefas.");

            dynamic service = serviceObject;
            service.Connect();
            rootObject = service.GetFolder("\\");
            string expectedPath = NormalizePath(Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName
                ?? "");
            if (expectedPath.Length == 0)
                return (0, 0, "Não foi possível localizar o executável para conferir a tarefa de logon.");

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ScanFolder(rootObject, expectedPath, visited, ref matching, ref elevated);
            return (matching, elevated, matching == 0
                ? "Nenhuma tarefa de logon com este executável foi localizada."
                : elevated == matching ? null : "Uma ou mais tarefas correspondentes não puderam ser elevadas.");
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            return (matching, elevated, ex.Message);
        }
        finally
        {
            ReleaseCom(rootObject);
            ReleaseCom(serviceObject);
        }
    }

    private static void ScanFolder(object folderObject, string executablePath, HashSet<string> visited,
        ref int matching, ref int elevated)
    {
        dynamic folder = folderObject;
        string folderPath = Convert.ToString(folder.Path, CultureInfo.InvariantCulture) ?? "\\";
        if (!visited.Add(folderPath)) return;

        object? tasksObject = null;
        object? foldersObject = null;
        try
        {
            tasksObject = folder.GetTasks(TaskEnumHidden);
            dynamic tasks = tasksObject;
            int taskCount = Convert.ToInt32(tasks.Count, CultureInfo.InvariantCulture);
            for (int index = 1; index <= taskCount; index++)
            {
                object? taskObject = null;
                object? definitionObject = null;
                object? actionsObject = null;
                try
                {
                    taskObject = tasks.Item(index);
                    dynamic task = taskObject!;
                    definitionObject = task.Definition;
                    dynamic definition = definitionObject!;
                    actionsObject = definition.Actions;
                    dynamic actions = actionsObject!;
                    bool matches = false;
                    int actionCount = Convert.ToInt32(actions.Count, CultureInfo.InvariantCulture);
                    for (int actionIndex = 1; actionIndex <= actionCount; actionIndex++)
                    {
                        object actionObject = actions.Item(actionIndex);
                        try
                        {
                            dynamic action = actionObject;
                            if (Convert.ToInt32(action.Type, CultureInfo.InvariantCulture) == 0
                                && NormalizePath(Convert.ToString(action.Path, CultureInfo.InvariantCulture) ?? "")
                                    .Equals(executablePath, StringComparison.OrdinalIgnoreCase))
                            {
                                matches = true;
                                break;
                            }
                        }
                        finally { ReleaseCom(actionObject); }
                    }

                    if (!matches) continue;
                    matching++;
                    dynamic principal = definition.Principal;
                    if (Convert.ToInt32(principal.RunLevel, CultureInfo.InvariantCulture) == TaskRunLevelHighest)
                    {
                        elevated++;
                        continue;
                    }

                    string principalId = Convert.ToString(principal.UserId, CultureInfo.InvariantCulture) ?? "";
                    if (principalId.Length == 0)
                        principalId = Convert.ToString(principal.GroupId, CultureInfo.InvariantCulture) ?? "";
                    if (!IsCurrentUserPrincipal(principalId))
                        continue;

                    principal.RunLevel = TaskRunLevelHighest;
                    string taskName = Convert.ToString(task.Name, CultureInfo.InvariantCulture) ?? "";
                    int logonType = Convert.ToInt32(principal.LogonType, CultureInfo.InvariantCulture);
                    folder.RegisterTaskDefinition(taskName, definition, TaskCreateOrUpdate,
                        Type.Missing, Type.Missing, logonType, Type.Missing);
                    elevated++;
                }
                catch (Exception ex) when (IsRecoverable(ex))
                {
                    WriteDiagnostic($"Não foi possível atualizar uma tarefa de logon do app: {ex.Message}");
                }
                finally
                {
                    ReleaseCom(actionsObject);
                    ReleaseCom(definitionObject);
                    ReleaseCom(taskObject);
                }
            }

            foldersObject = folder.GetFolders(0);
            dynamic folders = foldersObject;
            int folderCount = Convert.ToInt32(folders.Count, CultureInfo.InvariantCulture);
            for (int index = 1; index <= folderCount; index++)
            {
                object child = folders.Item(index);
                try { ScanFolder(child, executablePath, visited, ref matching, ref elevated); }
                finally { ReleaseCom(child); }
            }
        }
        finally
        {
            ReleaseCom(foldersObject);
            ReleaseCom(tasksObject);
        }
    }

    private static bool IsCurrentUserPrincipal(string principalId)
    {
        if (string.IsNullOrWhiteSpace(principalId)) return false;
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return principalId.Equals(identity.Name, StringComparison.OrdinalIgnoreCase)
                || principalId.Equals(identity.User?.Value, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (IsRecoverable(ex)) { return false; }
    }

    private static string NormalizePath(string path)
    {
        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return path; }
    }

    private static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            try { Marshal.FinalReleaseComObject(value); }
            catch (Exception ex) when (ex is InvalidComObjectException or ArgumentException) { }
        }
    }

    private static void WriteDiagnostic(string message)
    {
        try
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JungleLeopardStudio");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "sensor-diagnostics.log"), $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }

    private static bool IsRecoverable(Exception ex)
        => ex is not OutOfMemoryException and not StackOverflowException and not AccessViolationException;
}
