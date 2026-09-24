import { $ } from "./dom.js";

const SUCCESS_DURATION_MS = 3500;
const ERROR_DURATION_MS = 7000;

let hideTimer;

/** Shows a small message in the bottom-right corner; errors stay on screen a bit longer. */
export function toast(message, isError = false) {
  const element = $("#toast");
  element.textContent = message;
  element.className = "toast" + (isError ? " error" : "");

  clearTimeout(hideTimer);
  hideTimer = setTimeout(() => element.classList.add("hidden"), isError ? ERROR_DURATION_MS : SUCCESS_DURATION_MS);
}

/** Wraps an async handler so any error it throws is shown as a toast instead of being lost. */
export function guard(handler) {
  return async (...args) => {
    try {
      await handler(...args);
    } catch (error) {
      if (error.setupRequired) return; // the setup screen is already asking the user for the osu! folder
      toast(error.message, true);
    }
  };
}
