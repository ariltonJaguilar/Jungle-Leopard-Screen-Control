using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace JungleLeopardPoc;

internal static class AppLanguage
{
    private static readonly Dictionary<string, string> EnglishText = BuildEnglishText();
    private static readonly ConditionalWeakTable<DependencyObject, HashSet<DependencyProperty>> ObservedProperties = new();
    private static readonly Regex[] DynamicPatterns =
    [
        new Regex("^Tipo:\\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Fonte personalizada:\\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Página\\s+(\\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Encontrados\\s+(.+)\\s+temas oficiais\\. Selecione um para visualizar e instalar\\.$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Jogo em primeiro plano: (.+) \\(AppID (\\d+)\\)\\.$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Buscando a capa (vertical|horizontal) de (.+)…$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Capa (vertical|horizontal) carregada: (.+)\\. A transmissão acompanha o jogo em foco\\.$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Capa de (.+) pronta; tela Jungle Leopard não detectada\\.$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Não consegui obter a capa Steam: (.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Falha HTTP no catálogo: (.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Resposta JSON inválida: (.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Resposta incompatível: (.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Falha ao exportar: (.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Falha ao salvar pacote: (.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Não foi possível duplicar o tema: (.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Não foi possível excluir o tema: (.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Não foi possível instalar o tema: (.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Falha no download ou na validação: (.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Não foi possível renderizar os elementos do tema: (.+)\\. A imagem de prévia continua disponível\\.$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^A prévia não pôde ser decodificada: (.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Não foi possível abrir o layout Steam: (.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Não foi possível salvar o layout Steam: (.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Não foi possível adicionar a imagem: (.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Não foi possível usar a mídia de fundo: (.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Não consegui salvar essa preferência: (.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Não foi possível preparar a imagem: (.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Falha ao capturar o monitor: (.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Captura encerrada: (.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Falha ao gravar a mídia no controlador: (.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Não consegui encerrar o processo (\\d+): (.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Não consegui pedir ao Windows para encerrar o software elevado: (.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Não consegui reabrir o software original:\\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^A última imagem não pôde ser restaurada: (.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^O último tema \\.ht não pôde ser restaurado: (.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Falha ao atualizar a prévia dinâmica do tema: (.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Não foi possível iniciar o controle da tela: (.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Não foi possível carregar os arquivos do tema: (.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Aguarde a conclusão do download antes de continuar\\.$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Baixando pacote… (\\d+)%$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Baixando pacote… (.+) KB$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^“(.+)” foi instalada na biblioteca local\\.$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^“(.+)” foi duplicado\\. A cópia mantém os elementos do tema [.]ht\\.$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^“(.+)” foi removido da biblioteca local\\.$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Baixando “(.+)”…$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^“(.+)”\\.$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Grupo duplicado com (\\d+) camadas e subgrupos\\.$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Desbloqueie o grupo que contém “(.+)” para mover esta camada\\.$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Painel (\\d+) × (\\d+)  •  rotação (\\d+)°$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Painel (\\d+) × (\\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Marcadas (\\d+) camadas para exclusão em lote\\.$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Grupo criado com (\\d+) camadas\\. Selecione o grupo para ajustar seu deslocamento\\.$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Excluídas (\\d+) camadas marcadas e o conteúdo dos grupos selecionados\\.$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Composição ativa: (\\d+) painéis, arranjo (.+)\\.$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Painel conectado\\r?\\n(.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Painel não detectado(?:\\r?\\n(.+))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Firmware atualizado: versão (.+) \\(última publicada: (.+)\\)\\.$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Firmware (.+) disponível para (.+); o painel está na versão (.+)\\.$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Firmware atualizado e confirmado: versão (.+)\\.$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^O fabricante não publicou uma versão de firmware para (.+)\\.$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Não foi possível verificar o firmware: (.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Falha na atualização de firmware: (.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Não foi possível compor os painéis: (.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^FFmpeg selecionado: (.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^(.+)  •  prévia (\\d+) × (\\d+)  •  JPEG 4:2:0  •  (.+) KB$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Prévia indisponível: (.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Não foi possível assumir o painel para (.+)\\.$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Não consegui salvar essa preferência: (.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Capturando (.+) e transmitindo para (\\d+) painel\\(is\\) Pro Flow\\.$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^(\\d+) painel\\(is\\) detectado\\(s\\) automaticamente: (.+)\\.$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Ajustes atualizados\\. Prévia (\\d+) × (\\d+); quadro nativo do painel (\\d+) × (\\d+)\\.$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Prévia (\\d+) × (\\d+); quadro USB em (\\d+) × (\\d+)\\. Aplique um tema pelo Studio de temas para enviar conteúdo\\.$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Capturando (.+) e preparando o próximo quadro USB\\.$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Enviando “(.+)” para (.+)\\. A imagem será substituída no próximo quadro\\.$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Conectando a (.+) e transmitindo o conteúdo atual\\.$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Abrindo (.+) e iniciando a transmissão\\. Se o original estiver ativo, use “Fechar Jungle Leopard” primeiro\\.$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Solicitei o fechamento normal do software original\\. Aguardando a porta ser liberada…$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^O software original está ativo sem uma janela principal visível\\. Verificando se ele encerra…$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Reiniciando somente o dispositivo USB do painel em (.+)\\.\\.\\.$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^O Windows reportou reinicio pendente apos reiniciar (.+)\\. Tentando reconectar sem reiniciar o computador\\.\\.\\.$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Dispositivo USB em (.+) reiniciado\\. Aguardando a reconexao do painel\\.\\.\\.$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Nao foi possivel reiniciar a conexao USB: (.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Tema instalado • (.+) • indicadores dinâmicos • sensores sem leitura aparecem como —$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Tema instalado • (.+) • indicadores dinâmicos$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Pacote [.]ht • (.+) • indicadores dinâmicos • sensores sem leitura aparecem como —$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Pacote [.]ht • (.+) • indicadores dinâmicos$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Tema instalado • (.+) • cena ao vivo$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Pacote [.]ht • (.+) • cena ao vivo$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Tema instalado • (.+) • somente imagem estática; este arquivo não contém uma cena [.]ht[.]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Pacote [.]ht • (.+) • somente imagem estática; este arquivo não contém uma cena [.]ht[.]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^“(.+)” e seus arquivos de mídia foram empacotados em um [.]ht independente[.]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^“(.+)” foi importada[.] O pacote original foi preservado[.]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^“(.+)” foi criada como pacote [.]ht editável[.]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Excluir “(.+)” da biblioteca local[?] Essa ação remove o arquivo [.]ht[.]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^(.+) [(]indisponível[)]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^(\\d+) painéis detectados[.] É possível espelhar o quadro ou compor a captura entre eles[.]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Tela Jungle Leopard detectada na porta USB (.+)[.]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^(\\d+) telas Jungle Leopard detectadas nas portas USB (.+)[.]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^(\\d+) painéis conectados\\r?\\n(.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^FFmpeg selecionado: (.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Camada “(.+)” visível na prévia e no próximo [.]ht salvo[.]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Camada “(.+)” oculta na prévia e no próximo [.]ht salvo[.]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Camada “(.+)” bloqueada para mover e redimensionar[.]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Camada “(.+)” desbloqueada[.]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Camada movida para o grupo '(.+)'[.]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^FFmpeg não configurado; o vídeo original foi mantido e sua reprodução depende dos codecs do Windows[.] O arquivo será embutido no pacote [.]ht ao salvar[.]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Vídeo convertido para MP4/H.264, 4:2:0, 30 quadros/s e sem áudio para compatibilidade com temas [.]ht[.] O arquivo será embutido no pacote [.]ht ao salvar[.]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^A conversão FFmpeg falhou; o arquivo original foi mantido \\((.+)\\)[.] O arquivo será embutido no pacote [.]ht ao salvar[.]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Imagem (adicionada|substituída)[.] A nova mídia será embutida no [.]ht ao salvar[.]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Vídeo (adicionado|substituído)[.] A nova mídia será embutida no [.]ht ao salvar[.]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Prévia indisponível: (.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Não consegui reabrir o software original:\\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Nao encontrei o dispositivo USB correspondente a porta (.+)[.]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^O reinicio seletivo do dispositivo requer Windows 10 versao 2004 ou posterior[.]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^O Jungle Leopard Display continua aberto[.] Feche-o pelo botao 'Fechar Jungle Leopard' e tente novamente[.]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Solicitando ao Windows o reinicio de (.+) para destravar a comunicacao[.][.][.]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Desativando e reativando o dispositivo (.+) pelo Windows[.][.][.]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^O Windows nao iniciou a ferramenta de reinicio do dispositivo[.]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^O Windows nao conseguiu reiniciar o dispositivo USB [(]codigo (.+)[)][.]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^O Windows reiniciou (.+), mas informou que ha uma operacao pendente para reinicializacao[.] Testando a reconexao agora[.][.][.]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Dispositivo USB reiniciado[.] Aguardando (.+) reaparecer[.][.][.]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^USB reiniciado[.] Aguardando a transmissao antiga liberar a porta[.][.][.]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^Imagem restaurada: (.+)[.]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^O Windows voltou, mas não foi possível retomar a imagem no LCD: (.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
    ];

    private static readonly string[] DynamicTranslations =
    [
        "Type: $1", "Custom source: $1", "Page $1",
        "Found $1 official themes. Select one to preview and install.",
        "Foreground game: $1 (AppID $2).",
        "Fetching the $1 Steam cover for $2…",
        "$1 Steam cover loaded: $2. Streaming follows the foreground game.",
        "Cover for $1 is ready; Jungle Leopard display not detected.",
        "Could not retrieve the Steam cover: $1",
        "Catalog HTTP error: $1", "Invalid JSON response: $1", "Incompatible response: $1",
        "Export failed: $1", "Could not save the package: $1", "Could not duplicate the theme: $1",
        "Could not delete the theme: $1", "Could not install the theme: $1", "Download or validation failed: $1",
        "Could not render the theme elements: $1. The preview image is still available.",
        "Could not decode the preview: $1", "Could not open the Steam layout: $1",
        "Could not save the Steam layout: $1", "Could not add the image: $1",
        "Could not use the background media: $1", "Could not save this preference: $1",
        "Could not prepare the image: $1", "Monitor capture failed: $1", "Capture ended: $1",
        "Could not write media to the controller: $1", "Could not stop process $1: $2",
        "Could not ask Windows to stop the elevated software: $1", "Could not reopen the original software: $1",
        "Could not restore the last image: $1", "Could not restore the last .ht theme: $1",
        "Could not refresh the live theme preview: $1", "Could not start screen control: $1",
        "Could not load the theme files: $1", "Wait for the download to finish before continuing.",
        "Downloading package… $1%", "Downloading package… $1 KB", "“$1” was installed in the local library.",
        "“$1” was duplicated. The copy keeps the .ht theme elements.", "“$1” was removed from the local library.",
        "Downloading “$1”…", "“$1”.", "Duplicated group with $1 layers and subgroups.",
        "Unlock the group containing “$1” to move this layer.",
        "Panel $1 × $2  •  rotation $3°", "Panel $1 × $2",
        "Selected $1 layers for bulk deletion.",
        "Created a group with $1 layers. Select the group to adjust its position.",
        "Deleted $1 selected layers and the contents of selected groups.",
        "Arrangement active: $1 panels, layout $2.",
        "Panel connected\n$1", "Panel not detected$1",
        "Firmware updated: version $1 (latest published: $2).",
        "Firmware $1 is available for $2; the panel is running version $3.",
        "Firmware updated and verified: version $1.",
        "The manufacturer has not published a firmware version for $1.",
        "Could not check for firmware updates: $1", "Firmware update failed: $1",
        "Could not arrange the panels: $1", "Selected FFmpeg: $1",
        "$1  •  preview $2 × $3  •  JPEG 4:2:0  •  $4 KB", "Preview unavailable: $1",
        "Could not take control of the panel to $1.",
        "Could not save this preference: $1",
        "Capturing $1 and streaming to $2 Pro Flow panel(s).",
        "$1 Pro Flow panel(s) detected automatically: $2.",
        "Settings updated. Preview $1 × $2; panel's native frame $3 × $4.",
        "Preview $1 × $2; USB frame $3 × $4. Apply a theme in Cover Studio to send content.",
        "Capturing $1 and preparing the next USB frame.",
        "Sending “$1” to $2. The image will be replaced in the next frame.",
        "Connecting to $1 and streaming the current content.",
        "Opening $1 and starting the stream. If the original software is running, close Jungle Leopard first.",
        "Requested the original software to close normally. Waiting for the port to be released…",
        "The original software is running without a visible main window. Checking whether it exits…",
        "Restarting only the panel's USB device on $1...",
        "Windows reported a pending restart after restarting $1. Trying to reconnect without restarting the computer...",
        "USB device on $1 restarted. Waiting for the panel to reconnect...",
        "Could not restart the USB connection: $1",
        "Installed theme • $1 • dynamic indicators • unread sensors appear as —",
        "Installed theme • $1 • dynamic indicators",
        ".ht package • $1 • dynamic indicators • unread sensors appear as —",
        ".ht package • $1 • dynamic indicators",
        "Installed theme • $1 • live scene", ".ht package • $1 • live scene",
        "Installed theme • $1 • static preview image only; this file has no .ht scene.",
        ".ht package • $1 • static preview image only; this file has no .ht scene.",
        "“$1” and its media files were packaged as a standalone .ht file.",
        "“$1” was imported. The original package was preserved.",
        "“$1” was created as an editable .ht package.",
        "Delete “$1” from the local library? This will remove the .ht file.",
        "$1 (unavailable)",
        "$1 panels detected. You can mirror the frame or arrange a capture across them.",
        "Jungle Leopard display detected on USB port $1.",
        "$1 Jungle Leopard displays detected on USB ports $2.",
        "$1 panels connected\n$2", "Selected FFmpeg: $1",
        "Layer “$1” will be visible in the preview and next saved .ht theme.",
        "Layer “$1” will be hidden in the preview and next saved .ht theme.",
        "Layer “$1” is locked against moving and resizing.", "Layer “$1” unlocked.",
        "Layer moved into group '$1'.",
        "FFmpeg is not configured; the original video was kept and playback depends on Windows codecs. The file will be included in the .ht package when saved.",
        "Video converted to MP4/H.264, 4:2:0, 30 fps, and no audio for .ht theme compatibility. The file will be included in the .ht package when saved.",
        "FFmpeg conversion failed; the original file was kept ($1). The file will be included in the .ht package when saved.",
        "$1. The new media will be included in the .ht package when saved.",
        "$1. The new media will be included in the .ht package when saved.",
        "Preview unavailable: $1", "Could not reopen the original software: $1",
        "Could not find the USB device for port $1.",
        "Selective device restart requires Windows 10 version 2004 or later.",
        "Jungle Leopard Display is still open. Close it using the 'Close Jungle Leopard' button and try again.",
        "Asking Windows to restart $1 to release the connection...",
        "Disabling and re-enabling device $1 through Windows...",
        "Windows did not start the device restart tool.",
        "Windows could not restart the USB device (code $1).",
        "Windows restarted $1 but reported a restart operation is pending. Testing reconnection now...",
        "USB device restarted. Waiting for $1 to reappear...",
        "USB restarted. Waiting for the previous stream to release the port...",
        "Image restored: $1.",
        "Windows resumed, but the LCD image could not be restored: $1"
    ];

    public static bool IsPortuguese { get; } = DetectPortugueseWindowsLanguage();

    public static string Translate(string? text)
    {
        if (string.IsNullOrEmpty(text) || IsPortuguese) return text ?? string.Empty;
        string key = Normalize(text);
        if (EnglishText.TryGetValue(key, out string? translated)) return translated;
        for (int i = 0; i < DynamicPatterns.Length; i++)
        {
            Match match = DynamicPatterns[i].Match(text);
            if (!match.Success) continue;
            string template = DynamicTranslations[i];
            for (int group = match.Groups.Count - 1; group >= 1; group--)
                template = template.Replace("$" + group.ToString(CultureInfo.InvariantCulture), match.Groups[group].Value, StringComparison.Ordinal);
            return template;
        }
        return text;
    }

    public static void Apply(DependencyObject root)
    {
        if (IsPortuguese) return;
        Visit(root, new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance));
    }

    private static void Visit(DependencyObject element, HashSet<DependencyObject> visited)
    {
        if (!visited.Add(element)) return;
        if (element is TextBlock textBlock)
            LocalizeAndObserve(textBlock, TextBlock.TextProperty);
        if (element is ContentControl contentControl)
            LocalizeAndObserve(contentControl, ContentControl.ContentProperty);
        if (element is HeaderedContentControl headeredContent)
            LocalizeAndObserve(headeredContent, HeaderedContentControl.HeaderProperty);
        if (element is HeaderedItemsControl headeredItems)
            LocalizeAndObserve(headeredItems, HeaderedItemsControl.HeaderProperty);
        if (element is Window window)
            LocalizeAndObserve(window, Window.TitleProperty);
        if (element is FrameworkElement frameworkElement)
        {
            LocalizeAndObserve(frameworkElement, FrameworkElement.ToolTipProperty);
            LocalizeAndObserve(frameworkElement, AutomationProperties.NameProperty);
        }

        foreach (object child in LogicalTreeHelper.GetChildren(element))
            if (child is DependencyObject dependencyChild)
                Visit(dependencyChild, visited);
        try
        {
            int count = VisualTreeHelper.GetChildrenCount(element);
            for (int index = 0; index < count; index++)
                Visit(VisualTreeHelper.GetChild(element, index), visited);
        }
        catch (InvalidOperationException) { }
        catch (ArgumentException) { }
    }

    private static void LocalizeAndObserve(DependencyObject target, DependencyProperty property)
    {
        TranslatePropertyValue(target, property);
        HashSet<DependencyProperty> properties = ObservedProperties.GetOrCreateValue(target);
        if (!properties.Add(property)) return;
        DependencyPropertyDescriptor? descriptor = DependencyPropertyDescriptor.FromProperty(property, target.GetType());
        descriptor?.AddValueChanged(target, (_, _) => TranslatePropertyValue(target, property));
    }

    private static void TranslatePropertyValue(DependencyObject target, DependencyProperty property)
    {
        if (BindingOperations.IsDataBound(target, property) || ShouldPreserveDynamicValue(target, property)) return;
        if (target.GetValue(property) is not string source) return;
        string translated = Translate(source);
        if (!translated.Equals(source, StringComparison.Ordinal))
            target.SetCurrentValue(property, translated);
    }

    private static bool ShouldPreserveDynamicValue(DependencyObject target, DependencyProperty property)
    {
        if (target is not FrameworkElement element || property != TextBlock.TextProperty) return false;
        return element.Name is "PreviewTitle" or "ThemeNameBox" or "LayerNameBox";
    }

    private static bool DetectPortugueseWindowsLanguage()
    {
        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("pt", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> BuildEnglishText()
    {
        var translations = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(params (string Portuguese, string English)[] values)
        {
            foreach ((string portuguese, string english) in values)
                translations[Normalize(portuguese)] = english;
        }

        Add(
            ("Cover Studio", "Cover Studio"),
            ("Jungle Leopard", "Jungle Leopard"),
            ("Screen Control", "Screen Control"),
            ("Jungle Leopard Screen Control", "Jungle Leopard Screen Control"),
            ("Crie, organize e aplique temas no painel do Pro Flow 360.", "Create, organize, and apply themes on the Pro Flow 360 panel."),
            ("Importar tema", "Import theme"),
            ("Importar pacote .ht/.zip ou tema JSON instalado", "Import an .ht/.zip package or an installed JSON theme"),
            ("Novo tema", "New theme"),
            ("Minha biblioteca", "My Library"),
            ("Catálogo online", "Online Catalog"),
            ("Filtrar minha biblioteca", "Filter my library"),
            ("Buscar temas no catálogo oficial", "Search the official catalog"),
            ("Buscar", "Search"),
            ("Somente temas para tela cheia", "Full-screen themes only"),
            ("Sua biblioteca está vazia.\nImporte um pacote .ht ou crie um tema.", "Your library is empty.\nImport an .ht package or create a theme."),
            ("Os temas oficiais vão aparecer aqui.\nAbra o catálogo para carregar os resultados.", "Official themes will appear here.\nOpen the catalog to load results."),
            ("Anterior", "Previous"), ("Página 1", "Page 1"), ("Próxima", "Next"),
            ("Baixar e instalar", "Download and Install"),
            ("Temas", "Themes"),
            ("Importe um .ht, crie um tema ou explore o catálogo online.", "Import an .ht file, create a theme, or browse the online catalog."),
            ("Prévia do tema", "Theme Preview"),
            ("Selecione um tema na biblioteca ou no catálogo para visualizar o enquadramento do painel.", "Select a theme from your library or the catalog to preview how it fits the panel."),
            ("Ajustar tema para tela", "Fit theme to screen"),
            ("Desligado preserva a proporção e usa bordas pretas; ligado preenche o painel.", "Off preserves the original proportions with black borders when needed; on fills the panel."),
            ("Editar tema", "Edit Theme"), ("Duplicar", "Duplicate"), ("Excluir", "Delete"),
            ("Aplicar prévia à tela", "Apply Preview to Screen"), ("Exportar .ht", "Export .ht"),
            ("Orientação da tela", "Screen Orientation"),
            ("0°", "0°"), ("90° horário", "90° Clockwise"), ("180°", "180°"), ("270° horário", "270° Clockwise"),
            ("Brilho", "Brightness"), ("Biblioteca local do Studio.", "Studio local library."),
            ("EDITOR DE TEMAS", "THEME EDITOR"),
            ("Edite e arraste camadas no canvas do tema. A prévia usa os sensores disponíveis neste PC.", "Edit and drag layers on the theme canvas. The preview uses sensors available on this PC."),
            ("Nome", "Name"), ("CAMADAS", "LAYERS"), ("Texto", "Text"),
            ("Indicador de dados", "Data Indicator"), ("Letra atual", "Current Lyric"),
            ("Mídia", "Media"), ("Mídia e letras", "Media and Lyrics"), ("Bloco", "Block"),
            ("Barra de progresso", "Progress Bar"), ("Medidor circular", "Circular Gauge"),
            ("Gráfico de linha", "Line Chart"), ("Espectro de áudio", "Audio Spectrum"), ("Adicionar", "Add"),
            ("Marcar para excluir em lote", "Select for bulk deletion"), ("Segure e arraste para reordenar", "Drag to reorder"),
            ("CAMADA SELECIONADA", "SELECTED LAYER"),
            ("Criar uma cópia da camada selecionada", "Create a copy of the selected layer"),
            ("Excluir a camada selecionada", "Delete the selected layer"),
            ("AGRUPAMENTO E SELEÇÃO", "GROUPING AND SELECTION"),
            ("Agrupar", "Group"), ("Agrupar as camadas selecionadas", "Group the selected layers"),
            ("Desagrupar", "Ungroup"), ("Separar as camadas do grupo selecionado", "Ungroup the selected layers"),
            ("MARCAR", "SELECTION"), ("Todos", "All"), ("Marcar todas as camadas", "Select all layers"),
            ("Desmarcar", "Deselect"), ("Desmarcar todas as camadas", "Deselect all layers"),
            ("Inverter", "Invert"), ("Inverter a marcação das camadas", "Invert layer selection"),
            ("Excluir marcadas", "Delete Selected"), ("Excluir as camadas marcadas na lista", "Delete selected layers from the list"),
            ("Fundo", "Background"), ("PLANO DE FUNDO", "BACKGROUND"), ("Cor de base", "Base Color"),
            ("Escolher cor", "Choose Color"), ("Segunda cor do gradiente", "Gradient Secondary Color"),
            ("Enquadramento", "Fit"), ("Cobrir", "Cover"), ("Conter", "Contain"), ("Esticar", "Stretch"),
            ("Gradiente", "Gradient"), ("Linear", "Linear"), ("Circular", "Circular"), ("Sólido", "Solid"),
            ("Recorte da mídia de fundo (%)", "Background Media Crop (%)"),
            ("Largura", "Width"), ("Altura", "Height"),
            ("Escolher mídia de fundo…", "Choose Background Media…"), ("Remover mídia de fundo", "Remove Background Media"),
            ("Camada", "Layer"), ("Tipo", "Type"), ("Rotação (graus)", "Rotation (degrees)"),
            ("Fonte de dados", "Data Source"), ("Faixa mínima", "Minimum Range"), ("Faixa máxima", "Maximum Range"),
            ("ESTILO E FORMATAÇÃO", "STYLE AND FORMATTING"),
            ("Raio do canto (px)", "Corner Radius (px)"), ("Contorno (px)", "Outline (px)"),
            ("Espessura do arco", "Arc Thickness"), ("Abertura do arco (0-295°)", "Arc Opening (0–295°)"),
            ("Casas decimais (0-3)", "Decimal Places (0–3)"),
            ("Formato do relógio", "Clock Format"), ("Escolha um formato ou digite um padrão compatível", "Choose a format or enter a compatible pattern"),
            ("Formato da barra", "Bar Format"), ("Preenchimento", "Fill"), ("Segmentado", "Segmented"),
            ("Cor única", "Single Color"), ("Estilo da borda", "Border Style"), ("Contínua", "Solid"), ("Tracejada", "Dashed"),
            ("Gradiente da camada", "Layer Gradient"), ("Sem gradiente", "No Gradient"),
            ("Da esquerda para a direita", "Left to Right"), ("De cima para baixo", "Top to Bottom"),
            ("Contorno do texto (px)", "Text Outline (px)"), ("Cor do contorno", "Outline Color"),
            ("Deslocamento da sombra X", "Shadow Offset X"), ("Deslocamento da sombra Y", "Shadow Offset Y"),
            ("Desfoque da sombra (px)", "Shadow Blur (px)"), ("Cor da sombra", "Shadow Color"),
            ("Manter proporção ao redimensionar", "Keep proportions when resizing"),
            ("Ocultar unidade do indicador", "Hide indicator unit"), ("Exibir a letra atual da música", "Show the current song lyric"),
            ("Preencher área do gráfico", "Fill chart area"), ("Exibir eixos e valores", "Show axes and values"), ("Exibir linhas de grade", "Show gridlines"),
            ("MÍDIA E LETRAS", "MEDIA AND LYRICS"), ("Layout de mídia", "Media Layout"),
            ("Horizontal - capa fixa", "Horizontal – fixed cover"), ("Horizontal - capa centralizada", "Horizontal – centered cover"),
            ("Vertical - capa fixa", "Vertical – fixed cover"), ("Vertical - capa centralizada", "Vertical – centered cover"),
            ("Somente capa", "Cover only"), ("Somente letras", "Lyrics only"),
            ("Exibir capa", "Show cover"), ("Exibir título", "Show title"), ("Exibir artista", "Show artist"),
            ("Exibir letras", "Show lyrics"), ("Fundo do cartão", "Card Background"), ("Mostrar dica sem mídia/letras", "Show a hint when no media or lyrics are available"),
            ("Capa circular", "Circular cover"), ("Girar capa", "Rotate cover"), ("Rolar letras sincronizadas", "Scroll synchronized lyrics"),
            ("Tamanho da letra ativa (8-999 px)", "Active lyric size (8–999 px)"), ("Quantidade de linhas (1-99)", "Number of lines (1–99)"),
            ("Tamanho do título (8-999 px)", "Title size (8–999 px)"), ("Tamanho do artista (8-999 px)", "Artist size (8–999 px)"),
            ("Raio do fundo (0-240 px)", "Background Radius (0–240 px)"), ("Espaço entre linhas", "Line Spacing"),
            ("Cor do título", "Title Color"), ("Opacidade do fundo (0-1)", "Background Opacity (0–1)"),
            ("Preenchimento da capa", "Cover Fit"), ("Automático / conter", "Automatic / contain"),
            ("Preencher altura", "Fit height"), ("Preencher largura", "Fit width"),
            ("Duração da rotação (s)", "Rotation Duration (s)"), ("Espaço entre capa e conteúdo", "Spacing between cover and content"),
            ("As letras sincronizadas acompanham a posição da faixa; sem correspondência, título, artista e capa continuam disponíveis.", "Synchronized lyrics follow the track position; if no match is found, the title, artist, and cover remain available."),
            ("ESPECTRO DE ÁUDIO", "AUDIO SPECTRUM"), ("Quantidade de barras (20–256)", "Number of bars (20–256)"),
            ("Espaçamento (0–5)", "Spacing (0–5)"), ("Velocidade de queda (3–18)", "Fall speed (3–18)"),
            ("Pico no centro", "Peak in the center"), ("O espectro captura o áudio reproduzido pelo Windows. Sem áudio ativo, as barras ficam vazias.", "The spectrum captures audio playing through Windows. The bars remain empty when no audio is active."),
            ("FONTE DA MÍDIA", "MEDIA SOURCE"), ("Carregamento", "Loading"), ("Arquivo no tema", "File in theme"),
            ("URL da internet", "Internet URL"), ("URL HTTP ou HTTPS", "HTTP or HTTPS URL"),
            ("Mídia online é baixada em segundo plano e limitada a 90 MB.", "Online media is downloaded in the background and limited to 90 MB."),
            ("Cor (ex.: #FFFFFF)", "Color (e.g. #FFFFFF)"), ("Cor do trilho / fundo do gráfico", "Track / chart background color"),
            ("Família da fonte", "Font Family"), ("Negrito", "Bold"), ("Itálico", "Italic"), ("Sublinhado", "Underline"),
            ("Fonte (px)", "Font Size (px)"), ("Alinhamento", "Alignment"), ("Esquerda", "Left"), ("Centro", "Center"), ("Direita", "Right"),
            ("A cor aceita #RRGGBB ou R,G,B,A. A fonte precisa existir no Windows.", "Colors accept #RRGGBB or R,G,B,A. The font must be installed in Windows."),
            ("Prévia ao vivo usando os sensores disponíveis neste PC.", "Live preview using sensors available on this PC."),
            ("Cancelar", "Cancel"), ("Salvar pacote .ht", "Save .ht Package"),
            ("Controle da tela LCD", "LCD Screen Control"), ("Tela", "Display"), ("Procurando tela USB…", "Searching for USB display…"),
            ("Orientação da tela", "Screen Orientation"), ("90° anti-horário", "90° Counterclockwise"), ("Brilho", "Brightness"),
            ("Capa Steam automática", "Automatic Steam Cover"),
            ("Painel e conexão", "Panel and Connection"), ("Múltiplas telas", "Multiple Displays"),
            ("Dados dos temas", "Theme Data"), ("Integrações", "Integrations"), ("Configurações do LCD", "LCD Settings"),
            ("DISPOSITIVO", "DEVICE"), ("Procurando tela USB…", "Searching for USB display…"),
            ("Painel LCD e conexão", "LCD Panel and Connection"),
            ("Detecção USB automática e mídias de inicialização do Pro Flow 360.", "Automatic USB detection and startup media for Pro Flow 360."),
            ("TELA USB", "USB DISPLAY"), ("IDENTIFICAÇÃO DO PAINEL", "PANEL IDENTIFICATION"),
            ("Parar transmissão", "Stop Streaming"), ("Reiniciar porta USB", "Restart USB Port"),
            ("Fechar Jungle Leopard", "Close Jungle Leopard"), ("Parar e reabrir original", "Stop and Reopen Original"),
            ("CONTROLE DO PAINEL", "PANEL CONTROL"), ("FIRMWARE", "FIRMWARE"),
            ("Verificar atualizações", "Check for Updates"), ("Instalar atualização", "Install Update"),
            ("MÍDIA DE INICIALIZAÇÃO", "STARTUP MEDIA"), ("MÍDIA DE ESPERA", "STANDBY MEDIA"),
            ("Escolher arquivo…", "Choose File…"), ("Enviar ao painel", "Send to Panel"),
            ("Múltiplas telas LCD", "Multiple LCD Displays"),
            ("Organize os painéis detectados e envie o conteúdo atual para todos eles.", "Arrange detected panels and send the current content to all of them."),
            ("PAINÉIS COMPATÍVEIS DETECTADOS", "COMPATIBLE PANELS DETECTED"),
            ("Espelhar todos", "Mirror all"), ("Compor na horizontal", "Arrange horizontally"),
            ("Compor na vertical", "Arrange vertically"), ("Compor livremente", "Arrange freely"),
            ("ARRANJO DOS PAINÉIS", "PANEL ARRANGEMENT"), ("Mover para cima", "Move Up"), ("Mover para baixo", "Move Down"),
            ("RECORTE PERSONALIZADO POR PAINEL", "CUSTOM CROP PER PANEL"),
            ("Usar recorte personalizado neste painel", "Use a custom crop for this panel"),
            ("Largura (%)", "Width (%)"), ("Altura (%)", "Height (%)"), ("Posição X (px)", "Position X (px)"), ("Posição Y (px)", "Position Y (px)"),
            ("ORIENTAÇÃO DESTE PAINEL", "PANEL ORIENTATION"), ("Usar orientação global", "Use global orientation"),
            ("Aplicar recorte", "Apply Crop"), ("POSIÇÃO DOS PAINÉIS NO CANVAS", "PANEL POSITIONS ON CANVAS"),
            ("CAPTURAR UMA TELA DO WINDOWS", "CAPTURE A WINDOWS DISPLAY"), ("Monitor", "Display"),
            ("Incluir cursor na captura", "Include cursor in capture"), ("Iniciar captura", "Start Capture"),
            ("Parar captura", "Stop Capture"), ("Compor imagem/tema atual", "Arrange Current Image/Theme"),
            ("Parar composição", "Stop Arrangement"), ("DETECÇÃO USB", "USB DETECTION"),
            ("Sensores que cenas .ht podem mostrar ao vivo. Valores indisponíveis aparecem como —.", "Sensors that .ht scenes can display live. Unavailable readings appear as —."),
            ("Processador", "Processor"), ("Memória", "Memory"), ("Placa de vídeo", "Graphics Card"),
            ("Temperatura da CPU", "CPU Temperature"), ("Temperatura da GPU", "GPU Temperature"),
            ("Utilização atual", "Current Usage"), ("Memória física", "Physical Memory"), ("Sensor disponível", "Sensor Available"),
            ("Os temas atualizam os mostradores no painel. Fontes de sensor específicas podem ser escolhidas em Configurações do LCD.", "Themes update the gauges on the panel. Specific sensor sources can be selected in LCD Settings."),
            ("Preferências de renderização, sensores e execução do Jungle Leopard Screen Control.", "Rendering, sensor, and startup preferences for Jungle Leopard Screen Control."),
            ("Unidade de temperatura", "Temperature Unit"), ("Celsius (°C)", "Celsius (°C)"), ("Fahrenheit (°F)", "Fahrenheit (°F)"),
            ("Modo de renderização", "Rendering Mode"), ("Alta performance", "High Performance"), ("Equilibrado", "Balanced"), ("Alta qualidade", "High Quality"),
            ("Intervalo de leitura do disco", "Disk Polling Interval"), ("1 segundo", "1 second"), ("2 segundos", "2 seconds"), ("5 segundos", "5 seconds"), ("10 segundos", "10 seconds"),
            ("Desativar leitura de sensores de disco", "Disable disk sensor polling"),
            ("Manter reprodução durante bloqueio ou suspensão do Windows", "Keep playback active while Windows is locked or asleep"),
            ("Iniciar junto com o Windows", "Start with Windows"), ("Iniciar oculto na bandeja e minimizar ao fechar", "Start hidden in the system tray and minimize on close"),
            ("Selecionar ffmpeg.exe", "Select ffmpeg.exe"), ("Restaurar padrões", "Restore Defaults"),
            ("Integração", "Integration"), ("Configure conexões com serviços e recursos externos.", "Configure connections to external services and features."),
            ("STEAM", "STEAM"), ("Detecta o jogo Steam em primeiro plano e atualiza automaticamente a capa exibida no painel.", "Detects the Steam game in the foreground and automatically updates the cover shown on the panel."),
            ("Editar layout", "Edit Layout"),
            ("Selecione ‘Compor livremente’ para mover os painéis", "Select ‘Arrange freely’ to move panels"),
            ("Arraste os painéis para posicioná-los", "Drag panels to position them"),
            ("Painel conectado", "Panel connected"), ("Painel não detectado", "Panel not detected"),
            ("Escolher cor", "Choose Color"), ("Prévia", "Preview"), ("Cor RGB", "RGB Color"),
            ("Matiz", "Hue"), ("Saturação", "Saturation"), ("Luminosidade", "Brightness"), ("Opacidade", "Opacity"),
            ("Cores sugeridas", "Suggested Colors"), ("Aplicar", "Apply"),
            ("EDITOR DA INTEGRAÇÃO STEAM", "STEAM INTEGRATION EDITOR"),
            ("Posicione a capa Steam e adicione componentes para esta orientação.", "Position the Steam cover and add components for this orientation."),
            ("Salvar layout Steam", "Save Steam Layout"),
            ("O fundo acompanha a arte do jogo Steam e não pode ser alterado.", "The background follows the Steam game artwork and cannot be changed."),
            ("Outra fonte de dados (mantida do tema)", "Other data source (kept from theme)"),
            ("Rotação (graus)", "Rotation (degrees)"),
            ("Tamanho da letra (8-999 px)", "Lyric Size (8–999 px)"), ("Mensagem sem mídia", "Message when no media is available"),
            ("Cor das letras", "Lyric Color"), ("Cor da linha ativa", "Active Line Color"),
            ("Cor do artista", "Artist Color"), ("Cor de fundo do cartão", "Card Background Color"),
            ("Informe uma URL HTTP ou HTTPS válida para a mídia online.", "Enter a valid HTTP or HTTPS URL for online media."),
            ("Selecione uma camada", "Select a layer"), ("Camada removida da cena.", "Layer removed from the scene."),
            ("Informe um nome para o tema antes de salvar.", "Enter a theme name before saving."),
            ("A mídia excede 90 MB; reduza o arquivo para manter o pacote .ht em um tamanho compatível.", "Media exceeds 90 MB. Reduce the file size to keep the .ht package within the supported limit."),
            ("Preparando vídeo para o formato de temas .ht…", "Preparing video for the .ht theme format…"),
            ("Mídia de fundo adicionada.", "Background media added."),
            ("O arquivo será embutido no pacote .ht ao salvar.", "The file will be included in the .ht package when saved."),
            ("Mídia de fundo removida. A cor e o gradiente continuam aplicados.", "Background media removed. The color and gradient remain applied."),
            ("O fundo acompanha a arte do jogo Steam e não pode ser alterado.", "The background follows the Steam game artwork and cannot be changed."),
            ("Nenhum jogo compatível da Biblioteca Steam está em primeiro plano.", "No supported game from the Steam library is in the foreground."),
            ("A API oficial respondeu, mas não retornou pacotes para este filtro.", "The official API responded but returned no packages for this filter."),
            ("Não foi possível conectar ao catálogo. Verifique sua conexão e tente novamente.", "Could not connect to the catalog. Check your connection and try again."),
            ("O catálogo demorou para responder. Tente novamente.", "The catalog took too long to respond. Try again."),
            ("O catálogo respondeu em um formato que o Studio não reconheceu.", "The catalog returned a format that Studio did not recognize."),
            ("O catálogo retornou um endereço ou formato que o Studio não reconheceu.", "The catalog returned an address or format that Studio did not recognize."),
            ("A consulta do catálogo expirou após 25 segundos.", "The catalog request timed out after 25 seconds."),
            ("Conectando ao catálogo oficial…", "Connecting to the official catalog…"),
            ("Buscando temas no catálogo oficial…", "Searching the official catalog…"),
            ("Pesquisando temas compatíveis com o Pro Flow 360.", "Searching for themes compatible with Pro Flow 360."),
            ("Tema do catálogo oficial • baixe para instalar na biblioteca local.", "Official catalog theme • download it to install in your local library."),
            ("Tema instalado no Jungle Leopard Display; os arquivos originais são somente leitura.", "Theme installed in Jungle Leopard Display; original files are read-only."),
            ("Tema .ht aplicado. Os elementos compatíveis são atualizados em tempo real no painel.", "The .ht theme was applied. Compatible elements update live on the panel."),
            ("Pacote .ht exportado.", ".ht package exported."),
            ("Download concluído e pacote validado.", "Download complete and package validated."),
            ("Nenhum arquivo selecionado", "No file selected"), ("Nenhuma imagem selecionada", "No image selected"),
            ("Nenhuma mídia selecionada", "No media selected"), ("Pronto", "Ready"),
            ("O aplicativo será iniciado junto com o Windows.", "The app will start with Windows."),
            ("A inicialização automática foi desativada.", "Automatic startup has been disabled."),
            ("O Jungle Leopard Screen Control já está aberto nesta sessão do Windows. Use a janela existente para evitar disputa pela porta do painel LCD.", "Jungle Leopard Screen Control is already open in this Windows session. Use the existing window to avoid competing for the LCD panel port."),
            ("Jungle Leopard Screen Control já está em execução", "Jungle Leopard Screen Control is already running"),
            ("A Studio está assumindo o controle do painel e liberando a porta USB…", "Studio is taking control of the panel and releasing the USB port…"),
            ("A tela USB foi desconectada. A transmissão será interrompida quando a porta reportar a falha.", "The USB display was disconnected. Streaming will stop when the port reports the failure."),
            ("A reprodução está pausada porque o Windows está bloqueado ou suspenso.", "Playback is paused because Windows is locked or asleep."),
            ("Transmissão parada e porta liberada.", "Streaming stopped and port released."),
            ("Transmissão da Studio parada. Não encontrei o programa original para reabri-lo.", "Studio streaming stopped. The original program could not be found to reopen."),
            ("O software original continua ativo. A Studio só poderá usar a tela depois que ele for encerrado.", "The original software is still running. Studio can use the display after it is closed."),
            ("Jungle Leopard Display fechado. A porta da tela está livre para a Studio.", "Jungle Leopard Display closed. The display port is available to Studio."),
            ("Jungle Leopard Display encerrado. A porta da tela está livre para a Studio.", "Jungle Leopard Display stopped. The display port is available to Studio."),
            ("Ainda há processos do software original ativos. Feche-os pelo Windows e tente novamente.", "Some original software processes are still running. Close them in Windows and try again."),
            ("Aplique um tema pelo Studio de temas antes de iniciar a transmissão.", "Apply a theme from Cover Studio before starting streaming."),
            ("Conecte um painel Pro Flow antes de iniciar a captura do desktop.", "Connect a Pro Flow panel before starting desktop capture."),
            ("O Windows não informou um monitor disponível para capturar.", "Windows did not report an available display to capture."),
            ("Feche o Jungle Leopard Display e tente novamente.", "Close Jungle Leopard Display and try again."),
            ("Preferências locais restauradas para os valores padrão.", "Local preferences have been restored to their defaults."),
            ("O fundo acompanha a arte do jogo Steam e não pode ser alterado.", "The background follows the Steam game artwork and cannot be changed."),
            ("Cor de base", "Base Color"), ("Cor do trilho / fundo do gráfico", "Track / chart background color"),
            ("Bloquear camada para impedir movimento", "Lock layer to prevent movement"),
            ("Desbloquear camada para mover", "Unlock layer to move"),
            ("Bloqueada pelo grupo; desbloqueie o grupo para mover", "Locked by its group; unlock the group to move"),
            ("Ocultar camada", "Hide layer"), ("Mostrar camada", "Show layer"),
            ("Cópia", "Copy"), ("cópia", "copy"),
            ("Imagem", "Image"), ("Grupo", "Group"), ("Componente", "Component"),
            ("Outra fonte de dados (mantida do tema)", "Other data source (kept from theme)"),
            ("Nível", "Level"), ("nível", "level"),
            ("A prévia não pôde ser decodificada:", "Could not decode the preview:"),
            ("Prévia indisponível", "preview unavailable"),
            ("Catálogo oficial • explore e instale temas na biblioteca local.", "Official catalog • browse and install themes in your local library."),
            ("Minha biblioteca • temas locais e pacotes importados.", "My Library • local themes and imported packages."),
            ("Carregando temas…", "Loading themes…"),
            ("Nenhum jogo compatível da Biblioteca Steam está em primeiro plano.", "No supported game from the Steam library is in the foreground."),
            ("Ajustes atualizados.", "Settings updated."),
            ("Aguardando imagem", "Waiting for image"),
            ("Aguardando jogo Steam em primeiro plano", "Waiting for a Steam game in the foreground"),
            ("Media", "Media"),
            ("Sair", "Exit"), ("Abrir Jungle Leopard Screen Control", "Open Jungle Leopard Screen Control"),
            ("Importar tema Jungle Leopard", "Import a Jungle Leopard Theme"),
            ("Temas Jungle Leopard (*.ht;*.zip;*.json)|*.ht;*.zip;*.json|Pacotes .ht (*.ht)|*.ht|Arquivos ZIP (*.zip)|*.zip|Temas instalados (*.json)|*.json", "Jungle Leopard Themes (*.ht;*.zip;*.json)|*.ht;*.zip;*.json|.ht Packages (*.ht)|*.ht|ZIP Files (*.zip)|*.zip|Installed Themes (*.json)|*.json"),
            ("Exportar pacote Jungle Leopard", "Export Jungle Leopard Package"),
            ("Pacote Jungle Leopard (*.ht)|*.ht", "Jungle Leopard Package (*.ht)|*.ht"),
            ("Escolher mídia de fundo", "Choose Background Media"),
            ("Mídia compatível|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.mp4;*.m4v;*.wmv;*.avi;*.mov;*.mkv;*.webm|Todos os arquivos|*.*", "Supported Media|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.mp4;*.m4v;*.wmv;*.avi;*.mov;*.mkv;*.webm|All Files|*.*"),
            ("Adicionar imagem ao tema", "Add Media to Theme"),
            ("Mídia compatível|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.mp4;*.m4v;*.wmv;*.avi;*.mov;*.mkv;*.webm|Imagens|*.png;*.jpg;*.jpeg;*.bmp;*.gif|Vídeos|*.mp4;*.m4v;*.wmv;*.avi;*.mov;*.mkv;*.webm|Todos os arquivos|*.*", "Supported Media|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.mp4;*.m4v;*.wmv;*.avi;*.mov;*.mkv;*.webm|Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif|Videos|*.mp4;*.m4v;*.wmv;*.avi;*.mov;*.mkv;*.webm|All Files|*.*"),
            ("Escolha a mídia de espera", "Choose Standby Media"), ("Escolha a mídia de inicialização", "Choose Startup Media"),
            ("Mídia do painel|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.rgb565|Todos os arquivos|*.*", "Panel Media|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.rgb565|All Files|*.*"),
            ("Selecionar FFmpeg para normalizar vídeos de temas", "Select FFmpeg to Normalize Theme Videos"),
            ("Executável FFmpeg|ffmpeg.exe|Executáveis|*.exe|Todos os arquivos|*.*", "FFmpeg Executable|ffmpeg.exe|Executables|*.exe|All Files|*.*"),
            ("Escolha uma imagem para a tela", "Choose an Image for the Display"),
            ("Imagens|*.jpg;*.jpeg;*.png;*.bmp;*.gif|Todos os arquivos|*.*", "Images|*.jpg;*.jpeg;*.png;*.bmp;*.gif|All Files|*.*"),
            ("Configuração não aplicada", "Setting Not Applied"), ("Mídia necessária", "Media Required"),
            ("Tela em uso", "Display In Use"), ("Tela não detectada", "Display Not Detected"),
            ("Arquivo de mídia inválido", "Invalid Media File"), ("Falha ao gravar a mídia", "Media Write Failed"),
            ("Não foi possível abrir o arquivo de mídia", "Could Not Open Media File"),
            ("Tema não reconhecido", "Unrecognized Theme"), ("Não foi possível criar o tema", "Could Not Create Theme"),
            ("Não foi possível salvar o tema", "Could Not Save Theme"), ("Excluir tema", "Delete Theme"),
            ("Integração Steam", "Steam Integration"), ("Não foi possível adicionar a imagem", "Could Not Add Media"),
            ("Não foi possível assumir a tela", "Could Not Take Control of the Display"),
            ("Não foi possível restaurar a tela", "Could Not Restore the Display"),
            ("Falha ao abrir", "Could Not Open"), ("Tema necessário", "Theme Required"),
            ("Cena inválida:", "Invalid Scene:"),
            ("Não é possível mover um grupo para dentro dele mesmo ou de um descendente.", "A group cannot be moved into itself or one of its descendants."),
            ("Camada adicionada. Ajuste posição e conteúdo no painel de propriedades.", "Layer added. Adjust its position and content in the properties panel."),
            ("Camada removida da cena.", "Layer removed from the scene."),
            ("Selecione uma camada", "Select a layer"),
            ("Marcadas", "Selected"),
            ("Desmarcadas todas as camadas.", "All layers deselected."),
            ("Marcação das camadas invertida.", "Layer selection inverted."),
            ("A capa Steam obrigatória não pode ser agrupada.", "The required Steam cover cannot be grouped."),
            ("Selecione pelo menos duas camadas do mesmo grupo para agrupá-las.", "Select at least two layers in the same group to group them."),
            ("Selecione um grupo para desagrupá-lo.", "Select a group to ungroup it."),
            ("Grupo removido; as camadas mantiveram sua posição visual.", "Group removed; the layers kept their visual positions."),
            ("A cena não contém camadas marcadas.", "The scene has no selected layers."),
            ("Marque as caixas ao lado das camadas que deseja excluir.", "Select the checkboxes beside the layers you want to delete."),
            ("A capa Steam faz parte obrigatória do layout e não pode ser removida.", "The Steam cover is required by this layout and cannot be removed."),
            ("Camada duplicada. A cópia mantém sua mídia e fonte de dados.", "Layer duplicated. The copy keeps its media and data source."),
            ("Informe uma URL HTTP ou HTTPS válida para a mídia online.", "Enter a valid HTTP or HTTPS URL for online media."),
            ("O fabricante não publicou uma versão de firmware para", "The manufacturer has not published a firmware version for"),
            ("Consultando o manifesto de firmware do fabricante…", "Checking the manufacturer's firmware manifest…"),
            ("O painel não foi detectado na porta USB.", "The panel was not detected on the USB port."),
            ("O painel mudou desde a consulta. Verifique as atualizações novamente antes de instalar.", "The panel changed after the check. Check for updates again before installing."),
            ("Gravando firmware no painel. Não interrompa a conexão USB…", "Writing firmware to the panel. Do not disconnect USB…"),
            ("Firmware enviado. Aguardando o painel reiniciar e confirmar a versão…", "Firmware sent. Waiting for the panel to restart and confirm the version…"),
            ("O arquivo foi enviado, mas o painel não confirmou a nova versão. Não reenvie o firmware; confira a conexão e o software original.", "The file was sent, but the panel did not confirm the new version. Do not send the firmware again; check the connection and the original software."),
            ("Não foi possível assumir o painel para atualizar o firmware.", "Could not take control of the panel to update its firmware."),
            ("Parando a transmissão…", "Stopping the stream…"), ("Enviando arquivo original…", "Sending the original file…"),
            ("Gravada; controlador reiniciado.", "Written; controller restarted."), ("Falha no envio.", "Upload failed."),
            ("Pare a captura do Windows antes de compor o conteúdo selecionado.", "Stop Windows capture before arranging the selected content."),
            ("Conecte pelo menos dois painéis Pro Flow para compor uma imagem entre telas.", "Connect at least two Pro Flow panels to arrange an image across displays."),
            ("Aplique um tema pelo Studio de temas primeiro.", "Apply a theme in Cover Studio first."),
            ("Composição parada; o conteúdo voltou ao enquadramento normal da tela.", "Arrangement stopped; content returned to the display's normal fit."),
            ("A composição foi parada porque são necessários pelo menos dois painéis conectados.", "Arrangement stopped because at least two connected panels are required."),
            ("A composição foi parada porque são necessários pelo menos dois painéis conectados.", "Arrangement stopped because at least two connected panels are required."),
            ("Aplique um tema pelo Studio de temas antes de iniciar a transmissão.", "Apply a theme in Cover Studio before starting the stream."),
            ("Aplique um tema pelo Studio de temas antes de iniciar a transmissão.", "Apply a theme in Cover Studio before starting the stream."),
            ("Capa Steam automática desativada.", "Automatic Steam cover is disabled."),
            ("Selecione um arquivo de mídia primeiro.", "Select a media file first."),
            ("Não foi possível assumir o painel para gravar a mídia.", "Could not take control of the panel to write media."),
            ("Não encontrei um painel LCD Jungle Leopard compatível no USB. Confira o cabo.", "Could not find a compatible Jungle Leopard LCD panel over USB. Check the cable."),
            ("Não encontrei um painel LCD Jungle Leopard compatível no USB. Confira o cabo USB.", "Could not find a compatible Jungle Leopard LCD panel over USB. Check the USB cable."),
            ("A Studio está assumindo o controle do painel e liberando a porta USB…", "Studio is taking control of the panel and releasing the USB port…"),
            ("A tela USB foi desconectada. A transmissão será interrompida quando a porta reportar a falha.", "The USB display was disconnected. Streaming will stop when the port reports the failure."),
            ("O Windows não informou um monitor disponível para capturar.", "Windows did not report a display available for capture."),
            ("Feche o Jungle Leopard Display e tente novamente.", "Close Jungle Leopard Display and try again."),
            ("Pronto. Selecione uma imagem para começar.", "Ready. Select an image to get started."),
            ("Não foi possível assumir a tela. Feche o Jungle Leopard Display e tente novamente.", "Could not take control of the display. Close Jungle Leopard Display and try again."),
            ("Conecte um painel Pro Flow antes de iniciar a captura do desktop.", "Connect a Pro Flow panel before starting desktop capture."),
            ("Tema salvo como um novo pacote .ht; o original permaneceu na biblioteca.", "Theme saved as a new .ht package; the original remains in the library."),
            ("Prévia pronta. Temas instalados são abertos sem alterar os arquivos originais.", "Preview ready. Installed themes are opened without changing the original files."),
            ("Este tema contém apenas uma imagem de prévia; ele não traz camadas com mostradores para renderizar.", "This theme only contains a preview image; it has no layers with gauges to render."),
            ("O painel está em primeiro plano", "The panel is in the foreground"),
            ("O firmware está atualizado.", "Firmware is up to date."),
            ("Mídia gravada; a transmissão anterior foi retomada.", "Media written; the previous stream has resumed."),
            ("Mídia gravada; abra a transmissão novamente quando o painel estiver livre.", "Media written; restart streaming when the panel is available."),
            ("A gravação falhou", "Writing failed"),
            ("A transmissão anterior foi retomada.", "The previous stream has resumed."),
            ("e a transmissão não pôde ser retomada porque o painel não está disponível.", "and streaming could not resume because the panel is unavailable."),
            ("Mídia gravada, mas a transmissão anterior não foi retomada:", "Media written, but the previous stream did not resume:"),
            ("A gravação falhou (", "Writing failed ("),
            ("Caminho do FFmpeg salvo para a normalização de vídeos em temas .ht.", "FFmpeg path saved for normalizing videos in .ht themes."),
            ("Tema aplicado, mas não foi possível salvá-lo para a próxima abertura.", "Theme applied, but it could not be saved for the next launch."),
            ("Tema .ht ativo: relógio, utilização de CPU/memória/GPU e sensores de temperatura são atualizados ao vivo; sensores indisponíveis aparecem como —.", "Active .ht theme: clock, CPU/memory/GPU usage, and temperature sensors update live; unavailable sensors appear as —."),
            ("Último tema .ht restaurado na prévia. Aplique-o pelo Studio de temas para iniciar a transmissão.", "Last .ht theme restored in the preview. Apply it in Cover Studio to start streaming."),
            ("O software original está ativo na bandeja. Solicitando que ele mostre a janela…", "The original software is running in the system tray. Asking it to show its window…"),
            ("Jungle Leopard Display reaberto e trazido para a frente para retomar a tela.", "Jungle Leopard Display reopened and brought to the foreground to resume the display."),
            ("O Jungle Leopard Display não mostrou uma janela; reiniciando a instância oculta para devolver o controle da tela.", "Jungle Leopard Display did not show a window; restarting the hidden instance to return control of the display."),
            ("Iniciando Jungle Leopard Display…", "Starting Jungle Leopard Display…"),
            ("Jungle Leopard Display reaberto. Ele deve retomar o conteúdo configurado no próprio software.", "Jungle Leopard Display reopened. It should resume the content configured in its software."),
            ("A abertura normal não manteve o programa ativo. Tentando com a permissão elevada usada pela inicialização do software…", "The normal launch did not keep the program running. Retrying with the elevated permissions used at startup…"),
            ("O Windows iniciou o pedido, mas o software original não permaneceu aberto. Confira se a solicitação de acesso foi aprovada.", "Windows started the request, but the original software did not stay open. Check whether the access request was approved."),
            ("Transmissão da Studio parada; reabra o Jungle Leopard Display manualmente para retomar a tela.", "Studio streaming stopped; reopen Jungle Leopard Display manually to resume the display."),
            ("Parando a transmissão e liberando a porta serial…", "Stopping the stream and releasing the serial port…"),
            ("Cancelando a transmissao; o Windows vai reiniciar o dispositivo para liberar a porta...", "Canceling the stream; Windows will restart the device to release the port..."),
            ("Preparando o reinicio da conexao USB...", "Preparing to restart the USB connection..."),
            ("Fechando o Jungle Leopard Display para liberar a porta...", "Closing Jungle Leopard Display to release the port..."),
            ("Aguardando a resposta do Windows para reiniciar o dispositivo...", "Waiting for Windows to restart the device..."),
            ("O Windows aceitou o pedido. Aguardando o dispositivo USB...", "Windows accepted the request. Waiting for the USB device..."),
            ("Dispositivo USB reiniciado. Aguardando a porta reaparecer...", "USB device restarted. Waiting for the port to reappear..."),
            ("USB reiniciado. Aguardando a transmissao antiga liberar a porta...", "USB restarted. Waiting for the previous stream to release the port..."),
            ("LCD pausado durante o bloqueio/suspensão do Windows; a reprodução será retomada ao voltar.", "LCD paused while Windows is locked or asleep; playback will resume when it wakes."),
            ("Restaurar orientação, enquadramento, brilho e preferências locais para os valores padrão? As mídias de inicialização/espera já gravadas no painel serão preservadas.", "Restore the default orientation, fit, brightness, and local preferences? Startup and standby media already saved to the panel will be preserved."),
            ("Restaurar configurações", "Restore Settings"),
            ("Confirmar encerramento", "Confirm Shutdown"),
            ("O Jungle Leopard Display continuou ativo após o pedido de fechamento normal.\n\nEncerrá-lo à força pode descartar ajustes que ainda não foram salvos. Quer encerrar os processos restantes para liberar a tela?", "Jungle Leopard Display kept running after the normal close request.\n\nForce-closing it may discard unsaved changes. Do you want to end the remaining processes to release the display?"),
            ("Software original não encontrado", "Original Software Not Found"),
            ("Não encontrei o executável do Jungle Leopard Display. Instale-o ou abra-o manualmente para devolver o controle da tela.", "Could not find the Jungle Leopard Display executable. Install it or open it manually to return control of the display."),
            ("O Jungle Leopard Display continua ativo, mas não foi possível reabri-lo. Feche-o pelo ícone da bandeja ou execute a Studio com a mesma permissão e tente novamente.", "Jungle Leopard Display is still running, but it could not be reopened. Close it from the tray icon or run Studio with the same permissions and try again."),
            ("O aplicativo será iniciado junto com o Windows.", "The app will start with Windows."),
            ("A inicialização automática foi desativada.", "Automatic startup has been disabled."),
            ("Painel LCD Jungle Leopard", "Jungle Leopard LCD Panel"),
            ("horizontal", "horizontal"), ("vertical", "vertical"), ("espelhado", "mirrored"),
            ("Nenhum painel LCD Jungle Leopard compatível foi detectado. Conecte o painel por USB.", "No compatible Jungle Leopard LCD panel was detected. Connect the panel over USB."),
            ("1 painel detectado. Inicie uma imagem, tema ou captura do desktop na página Tela LCD.", "1 panel detected. Start an image, theme, or desktop capture on the LCD page."),
            ("É possível espelhar o quadro ou compor a captura entre eles.", "You can mirror the frame or arrange a capture across them."),
            ("Tela não encontrada. Conecte a tela Jungle Leopard por USB.", "Display not found. Connect the Jungle Leopard display over USB."),
            ("Painel desconectado", "Panel disconnected"),
            ("FFmpeg encontrado por busca automática.", "FFmpeg found automatically."),
            ("Sem FFmpeg: vídeos que o Windows não decodifica podem ficar sem imagem. Selecione ffmpeg.exe para habilitar o fallback.", "FFmpeg not found: videos that Windows cannot decode may appear blank. Select ffmpeg.exe to enable the fallback."),
            ("Busca automática: pasta do app, variável FFMPEG_PATH ou PATH do Windows", "Automatic search: app folder, FFMPEG_PATH variable, or Windows PATH"),
            ("Este item não publicou um arquivo de pacote para download.", "This item has no downloadable package file."),
            ("O pacote será salvo na biblioteca independente do Studio.", "The package will be saved in Studio's independent library."),
            ("Conectando ao servidor de temas…", "Connecting to the theme server…"),
            ("Ordem das camadas atualizada por arraste.", "Layer order updated by dragging."),
            ("A atualização interrompe a transmissão. Não desligue o computador nem desconecte o USB até a confirmação de conclusão.", "The update interrupts streaming. Do not turn off the computer or disconnect USB until completion is confirmed."),
            ("Atualização de firmware", "Firmware Update"),
            ("Firmware atualizado.", "Firmware is up to date."),
            ("O controlador não confirmou a nova versão.", "The controller did not confirm the new version."),
            ("O controlador foi reiniciado.", "The controller restarted."),
            ("Image", "Image")
        );

        AddDataSourceTranslations(translations);
        return translations;
    }

    private static void AddDataSourceTranslations(Dictionary<string, string> translations)
    {
        (string Portuguese, string English)[] values =
        [
            ("CPU", "CPU"), ("Placa de vídeo", "Graphics Card"), ("Memória", "Memory"), ("Disco", "Disk"),
            ("Sistema", "System"), ("Aplicativo em primeiro plano", "Foreground Application"),
            ("Modelo do processador", "Processor Model"), ("Uso da CPU (%)", "CPU Usage (%)"),
            ("Temperatura da CPU", "CPU Temperature"), ("Tensão da CPU", "CPU Voltage"),
            ("Consumo de energia da CPU", "CPU Power Draw"), ("Velocidade da ventoinha da CPU", "CPU Fan Speed"),
            ("Frequência da CPU", "CPU Frequency"), ("Modelo da placa de vídeo", "Graphics Card Model"),
            ("Uso da placa de vídeo (%)", "GPU Usage (%)"), ("Temperatura da placa de vídeo", "GPU Temperature"),
            ("Tensão da placa de vídeo", "GPU Voltage"), ("Consumo de energia da placa de vídeo", "GPU Power Draw"),
            ("Velocidade da ventoinha da placa de vídeo", "GPU Fan Speed"), ("Frequência da placa de vídeo", "GPU Frequency"),
            ("Tipo de memória", "Memory Type"), ("Uso da memória (%)", "Memory Usage (%)"),
            ("Temperatura da memória", "Memory Temperature"), ("Tensão da memória", "Memory Voltage"),
            ("Consumo de energia da memória", "Memory Power Draw"), ("Velocidade da ventoinha da memória", "Memory Fan Speed"),
            ("Frequência da memória", "Memory Frequency"), ("Memória usada (GB)", "Memory Used (GB)"),
            ("Memória disponível (GB)", "Memory Available (GB)"), ("Memória total (GB)", "Total Memory (GB)"),
            ("Nome da unidade de armazenamento", "Storage Drive Name"), ("Uso do disco (%)", "Disk Usage (%)"),
            ("Temperatura do disco", "Disk Temperature"), ("Tensão do disco", "Disk Voltage"),
            ("Consumo de energia do disco", "Disk Power Draw"), ("Velocidade da ventoinha do disco", "Disk Fan Speed"),
            ("Frequência do disco", "Disk Frequency"), ("Espaço usado no disco (GB)", "Disk Space Used (GB)"),
            ("Espaço livre no disco (GB)", "Disk Space Free (GB)"), ("Espaço total no disco (GB)", "Total Disk Space (GB)"),
            ("Gravação no disco (MB/s)", "Disk Write (MB/s)"), ("Leitura do disco (MB/s)", "Disk Read (MB/s)"),
            ("Modelo da placa-mãe", "Motherboard Model"), ("Velocidade da ventoinha da placa-mãe", "Motherboard Fan Speed"),
            ("Velocidade de envio pela rede", "Network Upload Speed"), ("Velocidade de recebimento pela rede", "Network Download Speed"),
            ("Data e hora (24 horas)", "Date and Time (24-hour)"), ("Data e hora (12 horas)", "Date and Time (12-hour)"),
            ("FPS do app em primeiro plano", "Foreground App FPS")
        ];
        foreach ((string portuguese, string english) in values)
            translations[Normalize(portuguese)] = english;
    }

    private static string Normalize(string value)
    {
        string collapsed = Regex.Replace(value.Replace('\r', ' ').Replace('\n', ' '), @"\s+", " ").Trim();
        string decomposed = collapsed.Normalize(NormalizationForm.FormD);
        var normalized = new StringBuilder(decomposed.Length);
        foreach (char character in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                normalized.Append(char.ToLowerInvariant(character));
        return normalized.ToString().Normalize(NormalizationForm.FormC);
    }
}
