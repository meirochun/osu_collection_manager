// Remembers small per-browser preferences. Storage can be blocked or full, so every access is wrapped:
// the page must keep working (just without memory) when it fails.

export function loadJson(key) {
  try {
    const text = localStorage.getItem(key);
    return text ? JSON.parse(text) : null;
  } catch {
    return null;
  }
}

export function saveJson(key, value) {
  try {
    localStorage.setItem(key, JSON.stringify(value));
  } catch {
    // Ignore: not being able to remember the form is not worth an error message.
  }
}
