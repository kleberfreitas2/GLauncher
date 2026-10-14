# Proxy Steam do GLauncher no Cloudflare Workers

O Worker mantém a chave Steam API no Cloudflare. O GLauncher envia somente o SteamID64 e recebe a resposta da API Steam; resultados bem-sucedidos ficam em cache por 15 minutos.

## Criar o Worker pelo GitHub

1. No Cloudflare, abra **Workers & Pages ? Create application ? Worker ? Connect to Git**.
2. Selecione o repositório `kleberfreitas2/GLauncher` e a branch `Feature-GameLauncher`.
3. Configure o diretório raiz como `cloudflare/steam-proxy` e o comando de implantação como `npx wrangler deploy`.
4. Crie o Worker e aguarde a primeira implantação.
5. Nas configurações **do novo Worker** (não no projeto Pages `print3dmanager`), abra **Settings ? Variables and Secrets** e adicione `STEAM_API_KEY` como **Secret**. Cole a chave Steam e salve; depois faça uma nova implantação.
6. Teste `https://<nome-do-worker>.<sua-conta>.workers.dev/health`; a resposta esperada é `{"status":"ok"}`.

A rota consumida pelo GLauncher é `https://<nome-do-worker>.<sua-conta>.workers.dev/api/steam/{SteamID64}/games`.

## Configurar o GLauncher

A URL-base do Worker precisa ser definida em `SteamLibraryProxyUrl` nas configurações locais durante os testes, ou configurada como valor padrão em `AppSettings` antes de distribuir uma versão. Não inclua `STEAM_API_KEY` no repositório nem no cliente desktop.

O secret que aparece nas configurações de outro projeto Cloudflare, como `print3dmanager`, não é compartilhado automaticamente com este Worker. Adicione-o novamente nas configurações do Worker do GLauncher.
