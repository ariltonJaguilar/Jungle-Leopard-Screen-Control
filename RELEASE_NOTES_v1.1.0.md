# Jungle Leopard Screen Control 1.1.0

Pacote de release para Windows x64.

## Novidades

- Interface em português ou inglês conforme o idioma do Windows.
- Editor visual de temas `.ht`, com prévia ao vivo, camadas editáveis e suporte a dados de hardware e mídia.
- Integração Steam que detecta o jogo em primeiro plano e escolhe arte vertical ou horizontal conforme a orientação do LCD.
- SteamGridDB como fallback quando a Steam não oferece uma capa compatível. A chave API é opcional, configurada em **Integrações > SteamGridDB** e protegida pelo Windows para a conta atual.
- Leitura integrada de sensores de hardware; o app inclui o instalador PawnIO para sensores que precisam do driver de baixo nível.
- Medição interna do FPS do app em primeiro plano com PresentMon incluído. Não requer RTSS.
- Controle de mais de um painel, composição, mídia e atualização de firmware compatível.

## Correções desta versão

- Melhorada a descoberta das capas Steam, incluindo dados locais `appinfo.vdf`, CDN e URLs públicas antes do fallback comunitário.
- A leitura de FPS usa o intervalo entre apresentações do jogo (`MsBetweenPresents`), em vez do intervalo entre inícios de quadros.
- Melhorados o encerramento e a limpeza das sessões ETW do PresentMon, com retentativa após falhas de inicialização.
- Adicionado status da captura de FPS para indicar o processo em primeiro plano, quadros recebidos e erros.
- Melhoradas a disponibilidade das leituras de CPU/GPU e a atualização da prévia de temas.

## Requisitos e instalação

- Windows 10 ou Windows 11, x64.
- Extraia todo o conteúdo do ZIP e execute `JungleLeopardScreenControl.exe`. O pacote inclui o runtime do .NET 10.
- Para buscar capas no SteamGridDB, configure uma chave API em **Integrações > SteamGridDB**. Capas encontradas pela Steam continuam funcionando sem essa chave.
- Alguns sensores de baixo nível pedem autorização do Windows para instalar o PawnIO. O FPS pode solicitar autorização para iniciar a captura PresentMon.

## Licenças

Consulte `LICENSE`, `THIRD-PARTY-NOTICES.md` e a pasta `LICENSES` incluídos no pacote.
