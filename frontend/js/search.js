// Search & Download tab: search the beatmap mirror, preview songs, and queue downloads.

import { $, $$, esc } from "./core/dom.js";
import { api } from "./core/api.js";
import { toast, guard } from "./core/toast.js";
import { formatLength, starBadge } from "./core/format.js";
import { watchJob } from "./jobs.js";

// The server returns up to 50 sets per request; a noticeably shorter page means we reached the end.
const PAGE_SIZE = 50;
const MIN_FULL_PAGE = 40;

let currentOffset = 0;
let hasMoreResults = false;
let isLoading = false;
let scrollObserver;
const selectedSetIds = new Set();

// ---------- rendering ----------

function renderCard({ set, coverUrl, previewUrl, installed }) {
  const isSelected = selectedSetIds.has(set.id);
  const setPage = `https://osu.ppy.sh/beatmapsets/${set.id}`;
  const cardImage = esc(coverUrl.replace("list.jpg", "card.jpg"));

  const difficulties = set.beatmaps
    .map(beatmap => `<span title="${esc(beatmap.version)} · ${Math.round(beatmap.bpm)} BPM · ${formatLength(beatmap.length)}">${starBadge(beatmap.stars)}</span>`)
    .join(" ");

  const selectControl = installed
    ? `<span class="installed">✔ installed</span>`
    : `<label><input type="checkbox" data-select="${set.id}" ${isSelected ? "checked" : ""}> select</label>`;

  // The whole cover (image + song name) is a link that opens the beatmap page in a new tab.
  return `
    <div class="card ${isSelected ? "selected" : ""}" data-id="${set.id}">
      <a class="cover" href="${setPage}" target="_blank" rel="noopener" title="Open on osu! website" style="background-image:url('${cardImage}')">
        <div class="ttl">
          <div class="title">${esc(set.artist)} - ${esc(set.title)}</div>
          <div class="sub cover-sub">mapped by ${esc(set.creator)} · ${set.status} · ♥ ${set.favouriteCount.toLocaleString()}</div>
        </div>
      </a>
      <div class="body">
        <div>${difficulties}</div>
        <div class="row">
          <span>${selectControl}</span>
          <span>
            <button class="link" data-preview="${previewUrl}">▶ preview</button>
            <a class="link accent-link" href="${setPage}" target="_blank" rel="noopener">osu! page</a>
          </span>
        </div>
      </div>
    </div>`;
}

// ---------- loading ----------

/** Shows the spinner while more results may exist (or are being fetched), hides it otherwise. */
function updateSpinner() {
  $("#searchSpinner").classList.toggle("hidden", !(hasMoreResults || isLoading));
}

/** Disables the search controls while a request is running, so nothing can be triggered twice. */
function setBusy(busy) {
  isLoading = busy;
  $("#searchBtn").disabled = busy;
  updateSpinner();
}

async function runSearch({ loadMore }) {
  const query = $("#searchQ").value.trim();
  if (!query || isLoading) return;

  setBusy(true);
  try {
    currentOffset = loadMore ? currentOffset + PAGE_SIZE : 0;
    if (!loadMore) {
      hasMoreResults = false;
      $("#searchResults").innerHTML = `<p class="muted">Searching…</p>`;
    }

    const ranked = $("#searchRanked").checked;
    const results = await api(`/search?q=${encodeURIComponent(query)}&offset=${currentOffset}&ranked=${ranked}`);
    const html = results.map(renderCard).join("");

    if (loadMore) {
      $("#searchResults").insertAdjacentHTML("beforeend", html);
    } else {
      $("#searchResults").innerHTML = html || `<p class="muted">No results.</p>`;
    }
    hasMoreResults = results.length >= MIN_FULL_PAGE;
  } catch (error) {
    // Stop auto-loading after a failure, otherwise the spinner would retry (and toast) in a loop.
    hasMoreResults = false;
    if (loadMore) currentOffset -= PAGE_SIZE;
    else $("#searchResults").innerHTML = "";
    throw error;
  } finally {
    setBusy(false);
    watchForMoreResults();
  }
}
const search = guard(runSearch);

/**
 * Loads the next page as soon as the spinner scrolls into view.
 * Re-observing after every load makes the browser report the spinner's visibility again,
 * so a page that doesn't fill the screen keeps loading until it does.
 */
function watchForMoreResults() {
  const spinner = $("#searchSpinner");
  scrollObserver.unobserve(spinner);
  if (hasMoreResults) scrollObserver.observe(spinner);
}

function updateDownloadButton() {
  const button = $("#dlBtn");
  button.disabled = !selectedSetIds.size;
  button.textContent = selectedSetIds.size ? `Download ${selectedSetIds.size} selected` : "Download selected";
}

function toggleAudioPreview(previewUrl) {
  const player = $("#player");
  if (player.src === previewUrl && !player.paused) {
    player.pause();
    return;
  }
  player.src = previewUrl;
  player.volume = 0.4;
  player.play();
}

export function initSearch() {
  scrollObserver = new IntersectionObserver(entries => {
    if (entries.some(entry => entry.isIntersecting) && hasMoreResults && !isLoading) {
      search({ loadMore: true });
    }
  }, { rootMargin: "200px" });

  $("#searchBtn").addEventListener("click", () => search({ loadMore: false }));
  $("#searchQ").addEventListener("keydown", event => {
    if (event.key === "Enter") search({ loadMore: false });
  });

  $("#searchResults").addEventListener("click", event => {
    const previewUrl = event.target.dataset.preview;
    if (previewUrl) toggleAudioPreview(previewUrl);
  });

  $("#searchResults").addEventListener("change", event => {
    const setId = Number(event.target.dataset.select);
    if (!setId) return;

    if (event.target.checked) selectedSetIds.add(setId);
    else selectedSetIds.delete(setId);
    event.target.closest(".card").classList.toggle("selected", event.target.checked);
    updateDownloadButton();
  });

  $("#dlBtn").addEventListener("click", guard(async () => {
    const collection = $("#dlColl").value.trim() || null;
    const { id } = await api("/download", { method: "POST", body: { setIds: [...selectedSetIds], collection } });

    toast(`Downloading ${selectedSetIds.size} sets. Progress is in the Jobs tab.`);
    selectedSetIds.clear();
    $$("#searchResults .card.selected").forEach(card => card.classList.remove("selected"));
    $$("#searchResults input[data-select]").forEach(checkbox => { checkbox.checked = false; });
    updateDownloadButton();
    watchJob(id);
  }));
}
