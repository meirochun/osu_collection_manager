// Export / import of a single collection as a small JSON file.
// Everything here is pure (no DOM, no network) except `downloadJson`, which triggers a browser download.

export const FILE_FORMAT = "osu-collection-manager";
export const FILE_VERSION = 1;

const MD5_PATTERN = /^[0-9a-f]{32}$/i;

/** First 8 hex characters of a fresh UUID, e.g. "3f2a9c1e". */
function shortId() {
  const uuid = globalThis.crypto?.randomUUID?.()
    ?? Array.from({ length: 32 }, () => Math.floor(Math.random() * 16).toString(16)).join("");
  return uuid.replace(/-/g, "").slice(0, 8);
}

/** "<username>_<shortid>.json", with characters Windows does not allow in file names replaced. */
export function exportFileName(username) {
  const safeName = String(username || "").replace(/[<>:"/\\|?*\u0000-\u001f]/g, "_").trim() || "osu";
  return `${safeName}_${shortId()}.json`;
}

/**
 * Builds the export document from the server's collection payload.
 * `md5` is the only field needed to restore a collection; the rest is there so a human
 * (or a future version of this tool) can tell what the maps were even if they aren't installed.
 */
export function buildExportFile(collection, username) {
  return {
    format: FILE_FORMAT,
    version: FILE_VERSION,
    exportedBy: username || null,
    name: collection.name,
    maps: collection.maps.map(({ md5, beatmap }) => ({
      md5,
      beatmapId: beatmap?.beatmapId ?? null,
      setId: beatmap?.setId ?? null,
      title: beatmap ? `${beatmap.artist} - ${beatmap.title} [${beatmap.version}]` : null,
    })),
  };
}

/** Saves `data` as a .json file through the browser's normal download flow. */
export function downloadJson(fileName, data) {
  const blob = new Blob([JSON.stringify(data, null, 2)], { type: "application/json" });
  const url = URL.createObjectURL(blob);

  const link = document.createElement("a");
  link.href = url;
  link.download = fileName;
  document.body.appendChild(link);
  link.click();
  link.remove();

  URL.revokeObjectURL(url);
}

/**
 * Reads the text of an exported file and returns `{ name, maps }`, where every map is `{ md5, beatmapId, setId }`
 * (the ids may be null). Throws an Error with a user-friendly message if the file is not one of ours.
 */
export function parseImportFile(text) {
  let data;
  try {
    data = JSON.parse(text);
  } catch {
    throw new Error("That file is not valid JSON.");
  }

  if (data?.format !== FILE_FORMAT) {
    throw new Error("That file is not an exported osu! collection.");
  }
  if (data.version > FILE_VERSION) {
    throw new Error("That file was exported by a newer version of this tool.");
  }

  const name = typeof data.name === "string" ? data.name.trim() : "";
  if (!name) {
    throw new Error("The file has no collection name.");
  }
  if (!Array.isArray(data.maps)) {
    throw new Error("The file has no map list.");
  }

  // Accept both { md5: "..." } entries and bare hash strings; drop anything malformed and duplicates.
  const byMd5 = new Map();
  for (const entry of data.maps) {
    const md5 = String(typeof entry === "string" ? entry : entry?.md5 ?? "").toLowerCase();
    if (!MD5_PATTERN.test(md5) || byMd5.has(md5)) continue;

    byMd5.set(md5, {
      md5,
      beatmapId: Number(entry?.beatmapId) > 0 ? Number(entry.beatmapId) : null,
      setId: Number(entry?.setId) > 0 ? Number(entry.setId) : null,
    });
  }

  return { name, maps: [...byMd5.values()] };
}
