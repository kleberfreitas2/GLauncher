const CACHE_TTL_SECONDS = 900;

export default {
  async fetch(request, env, context) {
    const url = new URL(request.url);

    if (request.method === "GET" && url.pathname === "/health") {
      return json({ status: "ok" });
    }

    if (request.method !== "GET") {
      return json({ error: "Metodo nao permitido." }, 405);
    }

    const match = url.pathname.match(/^\/api\/steam\/(\d{17})\/games$/);
    if (!match) {
      return json({ error: "Rota ou SteamID64 invalido." }, 400);
    }

    if (!env.STEAM_API_KEY) {
      return json({ error: "O segredo STEAM_API_KEY nao foi configurado no Worker." }, 503);
    }

    const steamId = match[1];
    const cacheKey = new Request(`${url.origin}/_cache/steam/${steamId}`);
    const cached = await caches.default.match(cacheKey);
    if (cached) {
      return cached;
    }

    const steamUrl = new URL("https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/");
    steamUrl.searchParams.set("key", env.STEAM_API_KEY);
    steamUrl.searchParams.set("steamid", steamId);
    steamUrl.searchParams.set("include_appinfo", "true");
    steamUrl.searchParams.set("include_played_free_games", "true");
    steamUrl.searchParams.set("format", "json");

    try {
      const upstream = await fetch(steamUrl.toString(), {
        headers: { Accept: "application/json" }
      });

      if (upstream.status === 401 || upstream.status === 403) {
        return json({ error: "A API Steam rejeitou a chave configurada no Worker." }, 502);
      }

      if (!upstream.ok) {
        return json({ error: "Falha ao consultar a API Steam." }, 502);
      }

      const response = new Response(await upstream.text(), {
        status: 200,
        headers: {
          "Content-Type": "application/json; charset=utf-8",
          "Cache-Control": `public, max-age=${CACHE_TTL_SECONDS}`
        }
      });

      context.waitUntil(caches.default.put(cacheKey, response.clone()));
      return response;
    } catch {
      return json({ error: "A API Steam esta indisponivel no momento." }, 502);
    }
  }
};

function json(body, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json; charset=utf-8" }
  });
}
