// Thin wrapper around fetch for the backend's JSON API (everything lives under /api).

/**
 * Calls the backend. Pass `body` as a plain object and it is sent as JSON.
 * Throws an Error carrying the server's message when the response is not OK.
 */
export async function api(path, options = {}) {
  const { body, ...fetchOptions } = options;

  const response = await fetch("/api" + path, {
    ...fetchOptions,
    headers: body ? { "Content-Type": "application/json" } : {},
    body: body ? JSON.stringify(body) : undefined,
  });

  const text = await response.text();
  let data = null;
  try {
    data = text ? JSON.parse(text) : null;
  } catch {
    // The server answered with something that is not JSON (e.g. a crash page); fall through to the status text.
  }

  if (!response.ok) {
    const error = new Error(data?.error || data?.title || `${response.status} ${response.statusText}`);
    if (data?.setupRequired) {
      // The osu! folder is missing or gone: let the setup screen take over instead of showing an error toast.
      error.setupRequired = true;
      document.dispatchEvent(new CustomEvent("setup-required"));
    }
    throw error;
  }
  return data;
}
