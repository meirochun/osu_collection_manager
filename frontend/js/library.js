// Library tab: browse and filter every difficulty installed in osu!, and add maps to a collection.

import { $, $$, esc } from "./core/dom.js";
import { api } from "./core/api.js";
import { toast, guard } from "./core/toast.js";
import { formatLength, formatBpm, starBadge, coverImage, beatmapLink } from "./core/format.js";
import { loadCollectionNames } from "./core/state.js";

const PAGE_SIZE = 50;
const FILTER_DEBOUNCE_MS = 250;

let currentPage = 1;
const selectedMd5s = new Set();

function buildQuery() {
  const params = new URLSearchParams({ page: currentPage, pageSize: PAGE_SIZE });

  const filters = {
    q: $("#libQ").value,
    minStars: $("#libMin").value,
    maxStars: $("#libMax").value,
    mode: $("#libMode").value,
    sort: $("#libSort").value,
  };
  for (const [key, value] of Object.entries(filters)) {
    if (value) params.set(key, value);
  }
  return params;
}

function renderRow(beatmap) {
  const lastPlayed = beatmap.lastPlayed
    ? " · last played " + new Date(beatmap.lastPlayed).toLocaleDateString()
    : "";

  return `
    <tr>
      <td><input type="checkbox" data-md5="${beatmap.md5}" ${selectedMd5s.has(beatmap.md5) ? "checked" : ""}></td>
      <td>${coverImage(beatmap.setId, beatmap.md5)}</td>
      <td>
        <div class="title">${beatmapLink(beatmap, `${esc(beatmap.artist)} - ${esc(beatmap.title)}`)}</div>
        <div class="sub">[${esc(beatmap.version)}] by ${esc(beatmap.creator)}${lastPlayed}</div>
      </td>
      <td>${starBadge(beatmap.starRating)}</td>
      <td>${formatBpm(beatmap)}</td>
      <td>${formatLength(beatmap.drainSeconds)}</td>
      <td class="sub">${beatmap.cs.toFixed(1)} / ${beatmap.ar.toFixed(1)} / ${beatmap.od.toFixed(1)}</td>
      <td class="sub">${beatmap.status}</td>
    </tr>`;
}

export const loadLibrary = guard(async () => {
  const result = await api("/library?" + buildQuery());
  const pageCount = Math.max(1, Math.ceil(result.total / PAGE_SIZE));

  const selectedText = selectedMd5s.size ? ` · ${selectedMd5s.size} selected` : "";
  $("#libInfo").textContent = `${result.total.toLocaleString()} difficulties${selectedText}`;
  $("#libPage").textContent = `Page ${currentPage} / ${pageCount}`;
  $("#libPrev").disabled = currentPage <= 1;
  $("#libNext").disabled = currentPage >= pageCount;

  $("#libRows").innerHTML = result.items.map(renderRow).join("");
  $("#libAll").checked = false;
  updateAddButton();
});

function updateAddButton() {
  const button = $("#libAdd");
  button.disabled = !selectedMd5s.size || !$("#libTarget").value;
  button.textContent = selectedMd5s.size ? `Add ${selectedMd5s.size} selected` : "Add selected";
}

function reloadFromFirstPage() {
  currentPage = 1;
  loadLibrary();
}

export function initLibrary() {
  // Text/number filters wait a moment after typing stops; dropdowns apply immediately.
  let debounceTimer;
  ["#libQ", "#libMin", "#libMax"].forEach(selector => {
    $(selector).addEventListener("input", () => {
      clearTimeout(debounceTimer);
      debounceTimer = setTimeout(reloadFromFirstPage, FILTER_DEBOUNCE_MS);
    });
  });
  ["#libMode", "#libSort"].forEach(selector => {
    $(selector).addEventListener("change", reloadFromFirstPage);
  });

  $("#libPrev").addEventListener("click", () => { currentPage--; loadLibrary(); });
  $("#libNext").addEventListener("click", () => { currentPage++; loadLibrary(); });

  // Row checkboxes (delegated, because rows are re-rendered on every page).
  $("#libRows").addEventListener("change", event => {
    const md5 = event.target.dataset.md5;
    if (!md5) return;
    if (event.target.checked) selectedMd5s.add(md5);
    else selectedMd5s.delete(md5);
    updateAddButton();
  });

  // "Select all" only affects the rows on the current page.
  $("#libAll").addEventListener("change", event => {
    $$("#libRows input[type=checkbox]").forEach(checkbox => {
      checkbox.checked = event.target.checked;
      if (event.target.checked) selectedMd5s.add(checkbox.dataset.md5);
      else selectedMd5s.delete(checkbox.dataset.md5);
    });
    updateAddButton();
  });

  $("#libTarget").addEventListener("change", updateAddButton);

  $("#libAdd").addEventListener("click", guard(async () => {
    const collectionName = $("#libTarget").value;
    await api("/collection", { method: "POST", body: { name: collectionName, add: [...selectedMd5s] } });

    toast(`Added ${selectedMd5s.size} maps to "${collectionName}"`);
    selectedMd5s.clear();
    $$("#libRows input[type=checkbox]").forEach(checkbox => { checkbox.checked = false; });
    updateAddButton();
    loadCollectionNames();
  }));
}
