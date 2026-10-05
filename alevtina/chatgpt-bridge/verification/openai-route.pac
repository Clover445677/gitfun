function FindProxyForURL(url, host) { host = host.toLowerCase().replace(/\.$/, '');
if ((host === "openai.com" || host.slice(-(10 + 1)) === '.' + "openai.com")) return 'PROXY 127.0.0.1:18881';
if ((host === "chatgpt.com" || host.slice(-(11 + 1)) === '.' + "chatgpt.com")) return 'PROXY 127.0.0.1:18881';
if ((host === "chat.com" || host.slice(-(8 + 1)) === '.' + "chat.com")) return 'PROXY 127.0.0.1:18881';
if ((host === "oaistatic.com" || host.slice(-(13 + 1)) === '.' + "oaistatic.com")) return 'PROXY 127.0.0.1:18881';
if ((host === "oaiusercontent.com" || host.slice(-(18 + 1)) === '.' + "oaiusercontent.com")) return 'PROXY 127.0.0.1:18881';
if ((host === "openaiapi-site.azureedge.net" || host.slice(-(28 + 1)) === '.' + "openaiapi-site.azureedge.net")) return 'PROXY 127.0.0.1:18881';
if ((host === "chatgpt.livekit.cloud" || host.slice(-(21 + 1)) === '.' + "chatgpt.livekit.cloud")) return 'PROXY 127.0.0.1:18881';
if ((host === "o33249.ingest.sentry.io" || host.slice(-(23 + 1)) === '.' + "o33249.ingest.sentry.io")) return 'PROXY 127.0.0.1:18881';
return 'DIRECT';
}
