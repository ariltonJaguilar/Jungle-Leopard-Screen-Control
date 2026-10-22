# Jungle Leopard Screen Control

Aplicativo Windows de código aberto para criar temas, monitorar dados do computador e controlar telas LCD compatíveis da Jungle Leopard, incluindo o painel Pro Flow 360. O programa oferece uma interface WPF própria e envia conteúdo ao painel pela conexão USB serial.

> O suporte depende do modelo, da controladora e do firmware. O projeto não inclui o programa proprietário Jungle Leopard Display nem depende dele para editar ou renderizar temas.

## Destaques

- **Capas da Steam no LCD:** detecta o jogo em primeiro plano, busca a arte correspondente e a atualiza automaticamente no painel. O editor permite personalizar layouts separados para telas em retrato e paisagem.

## Funcionalidades

- A interface usa português quando o idioma de exibição do Windows é português e inglês nos demais casos.

### Cover Studio e temas `.ht`

- Mantém uma biblioteca local de temas, além de permitir pesquisar e baixar temas do catálogo online.
- Importa e exporta pacotes `.ht`; também pode abrir temas instalados em formato JSON com os respectivos arquivos de mídia.
- Cria temas novos e edita cópias, preservando os arquivos de origem.
- Permite adicionar, organizar, ocultar, bloquear, duplicar, agrupar e reordenar camadas.
- Tipos de camada incluem imagem, vídeo, texto, dado/sensor, bloco de cor, barra, arco, gráfico de linha, espectro de áudio e informações de mídia/letras.
- Edita posição, tamanho, rotação, família tipográfica (por uma lista suspensa de fontes do Windows), cores, preenchimento, contorno e sombra. Fontes declaradas por temas importados continuam visíveis mesmo quando não estão instaladas. No canvas, camadas podem ser selecionadas, movidas, redimensionadas e giradas.
- A prévia é renderizada no PC; leituras de hardware podem diferir das leituras disponíveis no computador que executa o painel.

### Integração com Steam

- Detecta o jogo em primeiro plano e troca automaticamente a arte exibida no LCD. Também reconhece atalhos não Steam cadastrados na biblioteca Steam quando há arte personalizada disponível.
- Escolhe arte vertical ou horizontal conforme a orientação ativa do painel.
- O editor da integração mantém layouts separados para retrato e paisagem. A capa Steam obrigatória pode ser posicionada e redimensionada; o layout aceita outras camadas.
- O fundo da exibição Steam é preto. A opção de capa automática pode ser ligada ou desligada em **Integrações**.
- A arte é procurada primeiro nos arquivos locais da Steam e, quando aplicável, pode ser baixada da internet.

### Painel e múltiplas telas

- Detecta painéis compatíveis nas portas seriais e exibe estado, resolução, orientação e conexão.
- Ajusta orientação, brilho e enquadramento. Temas podem manter a proporção original com áreas pretas ou ser ajustados para preencher a tela.
- Controla mais de um painel em modos espelhado, horizontal, vertical ou livre. O compositor permite ajustar a posição, orientação e recorte de cada painel.
- Envia imagens e temas renderizados, incluindo fundos, camadas animadas, indicadores e gráficos.
- A página **Painel e conexão** inclui controles para a sessão USB e mídia de inicialização/espera. Há também uma opção para consultar e instalar atualizações de firmware compatíveis com a identidade do painel.

### Sensores e dados

Os temas podem mostrar, quando o hardware e o provedor publicam a leitura:

- uso, temperatura, frequência, potência e outros dados de CPU e GPU;
- memória, espaço e atividade do disco, rede e ventoinhas;
- relógio, informações da sessão de mídia e letras sincronizadas;
- FPS do aplicativo/jogo em primeiro plano.

O Windows e a biblioteca LibreHardwareMonitor integrada fornecem a maior parte dos sensores; alguns dados de GPU também podem vir de `nvidia-smi` ou WMI. **Alguns sensores que precisam de acesso de baixo nível ao hardware exigem o driver PawnIO.** O instalador oficial e assinado está incluído no aplicativo; instale-o uma vez pela página **Dados dos temas**, no botão **Configurar sensores de hardware**, e autorize a solicitação do Windows. Para ler esses sensores ao iniciar com o Windows, o aplicativo também configura a tarefa de logon para executar com privilégios mais altos. O driver permanece instalado após reiniciar. Não é necessário manter o Jungle Leopard Display ou outro programa de monitoramento aberto. FPS é medido pelo PresentMon incluído no projeto. A disponibilidade e a precisão de temperaturas, ventoinhas, potência e outros sensores variam conforme o hardware, o driver e as permissões. Uma fonte sem leitura deve ser tratada como indisponível, não como uma medição válida.

### Preferências e mídia

- Configura unidade de temperatura, modo de renderização, intervalo de leitura de disco, execução com o Windows e comportamento ao minimizar/fechar.
- Vídeos podem ser convertidos para um formato adequado ao painel com FFmpeg. O FFmpeg é opcional; sem ele, a reprodução depende dos codecs instalados no Windows.
- A camada de áudio pode exibir um espectro baseado no dispositivo de reprodução padrão.
- Informações de mídia são obtidas das sessões do Windows. A consulta de letras sincronizadas usa LRCLIB e requer conexão com a internet.

## Requisitos

### Para executar

- Windows 10 ou Windows 11, 64 bits.
- .NET 10 Desktop Runtime. O pacote publicado abaixo é framework-dependent e não inclui o runtime.
- Painel Jungle Leopard compatível conectado por USB para controlar um LCD. Recursos de biblioteca, edição e prévia também podem ser usados sem o painel.

### Para compilar

- Windows 10 ou 11.
- .NET 10 SDK com os componentes de desktop/WPF.
- Acesso ao NuGet na primeira restauração das dependências.
- Git, caso o código seja clonado deste repositório.

Dependências principais são declaradas em `JungleLeopardPoc.csproj`. LibreHardwareMonitorLib é restaurado pelo NuGet. O executável do PresentMon e os avisos de licença correspondentes ficam em `ThirdParty/PresentMon/`.

### Opcionais

- FFmpeg para converter mídias de vídeo. Configure `FFMPEG_PATH`, coloque `ffmpeg.exe` no `PATH` ou selecione o executável nas configurações do aplicativo.
- Visual Studio Build Tools com compilador C++ x64 e Windows SDK para recompilar a ponte nativa de sessões de mídia (`MediaSessionBridge.dll`). A DLL é opcional; o código-fonte e o script estão no repositório.

## Compilar e executar

No PowerShell, a partir da pasta do projeto:

```powershell
dotnet restore .\JungleLeopardPoc.csproj
dotnet build .\JungleLeopardPoc.csproj -c Release
```

Para publicar uma versão Windows x64 na pasta `build\release`:

```powershell
dotnet publish .\JungleLeopardPoc.csproj `
  -c Release `
  -r win-x64 `
  --self-contained false `
  -o .\build\release
```

O executável será `build\release\JungleLeopardScreenControl.exe`. Para executá-lo diretamente durante o desenvolvimento:

```powershell
dotnet run --project .\JungleLeopardPoc.csproj
```

Se quiser incluir a ponte nativa de sessões de mídia no próximo build, compile-a antes de publicar:

```powershell
.\BuildMediaSessionBridge.ps1
dotnet publish .\JungleLeopardPoc.csproj -c Release -r win-x64 --self-contained false -o .\build\release
```

## Dados locais

Preferências, temas importados e layouts da Steam são gravados no perfil do usuário, sob `%LOCALAPPDATA%\JungleLeopardStudio`. Esses dados não fazem parte do repositório. O `.gitignore` também exclui configurações locais, logs, caches e saídas de compilação.

## Estrutura do código

| Arquivo | Responsabilidade |
| --- | --- |
| `Program.cs` | Inicialização, conexão serial, transmissão, configurações, sensores e integração Steam. |
| `LcdStudioWindow.xaml` e `.cs` | Janela principal, navegação e páginas de controle. |
| `CoverStudioWindow.xaml` e `.cs` | Biblioteca, catálogo e prévia do Cover Studio. |
| `HtThemeEditorWindow.xaml` e `.cs` | Editor visual de camadas e propriedades de temas `.ht`. |
| `HtThemeRenderer.cs` | Renderização das cenas e camadas dos temas. |
| `ThemePackages.cs` | Leitura, criação, validação e escrita de pacotes de tema. |
| `SteamIntegration.cs` | Descoberta de jogos e obtenção de capas da Steam. |
| `ForegroundFpsSensor.cs` | Captura de FPS do processo em primeiro plano usando o PresentMon empacotado. |
| `MediaSessionService.cs` e `MediaSessionBridge.cpp` | Leitura de sessões de mídia do Windows e ponte nativa opcional. |
| `Win11Theme.xaml` | Estilos e recursos visuais compartilhados da interface. |
| `JungleLeopardPoc.csproj` | Plataforma, dependências e configuração de compilação/publicação. |

## Limitações e compatibilidade

- A presença de um sensor no seletor não garante que todo computador publique um valor para ele; hardware e drivers diferem. O aplicativo não deve interpretar ausência de leitura como zero.
- Sensores de FPS exigem um jogo/aplicativo com quadros apresentados e podem solicitar elevação para iniciar a captura ETW.
- O catálogo de temas, downloads de capas, firmware e letras sincronizadas requerem internet.
- Atualizações de firmware escrevem no painel. Confirme o modelo e a versão apresentados pelo dispositivo e não interrompa o processo de gravação.
- `painel-teste.jpg` é uma imagem de diagnóstico que pode ser usada para verificar enquadramento e transmissão.

## Licença

O código do projeto está sob a licença MIT; consulte [LICENSE](LICENSE). Dependências e ferramentas de terceiros têm suas próprias licenças, listadas em [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) e nos arquivos de licença correspondentes.
