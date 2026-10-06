// Starts Blazor with a reconnect policy that copes with the PocketID (forward-auth) session expiring.
//
// When the SSO session has expired, Traefik answers every request, including Blazor's /_blazor/negotiate, with a
// 302 to the identity provider. The browser can't follow that cross-origin redirect from fetch, so Blazor just
// keeps retrying forever. Before each retry we probe the page itself: if it redirects, the login has expired and a
// full page reload is the only thing that helps (it goes through the normal SSO flow and comes back here).
(() => {
  let probing = false;

  async function reloadIfLoggedOut() {
    if (probing) return;
    probing = true;
    try {
      const resp = await fetch(location.href, { method: 'HEAD', redirect: 'manual', cache: 'no-store', credentials: 'same-origin' });
      // 'opaqueredirect' = the proxy sent us to the login page.
      if (resp.type === 'opaqueredirect' || resp.status === 401 || resp.status === 403) location.reload();
    } catch {
      // Network error / server down (e.g. dotnet watch restarting): keep letting Blazor retry.
    } finally {
      probing = false;
    }
  }

  Blazor.start({
    circuit: {
      reconnectionOptions: {
        maxRetries: 60,
        retryIntervalMilliseconds: (previousAttempts) => {
          reloadIfLoggedOut();
          return previousAttempts < 5 ? 2000 : 5000;
        },
      },
    },
  });

  // Coming back to a long-idle tab: check straight away rather than waiting for the next retry.
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible' && document.getElementById('components-reconnect-modal')) reloadIfLoggedOut();
  });
})();
