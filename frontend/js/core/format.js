// Helpers that turn beatmap data into small pieces of HTML / text.

/** 185 seconds -> "3:05" */
export function formatLength(totalSeconds) {
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = String(totalSeconds % 60).padStart(2, "0");
  return `${minutes}:${seconds}`;
}

/** "120–180" for a BPM range, or just the single value when the map has a constant BPM. */
export function formatBpm(beatmap) {
  const isRange = beatmap.minBpm && beatmap.minBpm !== beatmap.maxBpm;
  return isRange ? `${beatmap.minBpm}–${beatmap.maxBpm}` : beatmap.maxBpm;
}

// ---------- star rating colours (osu!'s difficulty spectrum) ----------

const DIFFICULTY_COLOR_STOPS = [
  [0.1, "#4290fb"], [1.25, "#4fc0ff"], [2, "#4fffd5"], [2.5, "#7cff4f"], [3.3, "#f6f05c"], [4.2, "#ff8068"],
  [4.9, "#ff4e6f"], [5.8, "#c645b8"], [6.7, "#6563de"], [7.7, "#18158e"], [9, "#000000"],
];

function hexToRgb(hex) {
  return [1, 3, 5].map(start => parseInt(hex.slice(start, start + 2), 16));
}

function rgbToHex(rgb) {
  return "#" + rgb.map(channel => Math.round(channel).toString(16).padStart(2, "0")).join("");
}

/** Blends between the two colour stops surrounding `rating`. */
export function starRatingColor(rating) {
  const [firstStop, firstColor] = DIFFICULTY_COLOR_STOPS[0];
  if (rating <= firstStop) return firstColor;

  for (let i = 1; i < DIFFICULTY_COLOR_STOPS.length; i++) {
    const [upperStop, upperColor] = DIFFICULTY_COLOR_STOPS[i];
    const [lowerStop, lowerColor] = DIFFICULTY_COLOR_STOPS[i - 1];
    if (rating > upperStop) continue;

    const progress = (rating - lowerStop) / (upperStop - lowerStop);
    const from = hexToRgb(lowerColor);
    const to = hexToRgb(upperColor);
    return rgbToHex(from.map((value, channel) => value + (to[channel] - value) * progress));
  }
  return "#000000";
}

/** The coloured "5.43★" pill. */
export function starBadge(rating) {
  const textColor = rating >= 6.5 ? "#f6f05c" : "#111";
  return `<span class="stars" style="background:${starRatingColor(rating)};color:${textColor}">${rating.toFixed(2)}★</span>`;
}

// ---------- images & links ----------

/**
 * Small cover thumbnail. Prefers the official cover from osu!'s CDN; if that fails to load,
 * the image error handler (see `installImageFallbacks`) switches to the local background.
 */
export function coverImage(setId, md5) {
  const localBackground = md5 ? `/api/library/${md5}/background` : null;

  if (setId > 0) {
    const fallback = localBackground ? ` data-fallback="${localBackground}"` : "";
    return `<img class="thumb" loading="lazy" src="https://assets.ppy.sh/beatmaps/${setId}/covers/list.jpg"${fallback}>`;
  }
  if (localBackground) {
    return `<img class="thumb" loading="lazy" src="${localBackground}">`;
  }
  return `<div class="thumb"></div>`;
}

/**
 * Image `error` events don't bubble, so one capturing listener on the document handles every thumbnail:
 * swap in the fallback source once, and hide the image if that fails too.
 */
export function installImageFallbacks() {
  document.addEventListener("error", event => {
    const image = event.target;
    if (!(image instanceof HTMLImageElement) || !image.classList.contains("thumb")) return;

    const fallback = image.dataset.fallback;
    if (fallback) {
      delete image.dataset.fallback;
      image.src = fallback;
    } else {
      image.style.visibility = "hidden";
    }
  }, true);
}

/** Wraps `html` in a link to the beatmap's osu! page when we know its id. */
export function beatmapLink(beatmap, html) {
  if (!(beatmap.beatmapId > 0)) return html;
  return `<a class="plain-link" href="https://osu.ppy.sh/b/${beatmap.beatmapId}" target="_blank" rel="noopener">${html}</a>`;
}
