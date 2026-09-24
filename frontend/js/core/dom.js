// Tiny DOM helpers used everywhere in the UI.

/** First element matching a CSS selector (inside `root`, defaults to the whole page). */
export function $(selector, root = document) {
  return root.querySelector(selector);
}

/** All elements matching a CSS selector, as a real array. */
export function $$(selector, root = document) {
  return [...root.querySelectorAll(selector)];
}

const HTML_ESCAPES = { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" };

/** Escapes text so it is safe to drop into an HTML template string. */
export function esc(value) {
  return String(value ?? "").replace(/[&<>"']/g, char => HTML_ESCAPES[char]);
}
