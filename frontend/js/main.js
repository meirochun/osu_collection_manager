// Entry point: connects the tabs together and kicks off the first data loads.

import { $ } from "./core/dom.js";
import { installImageFallbacks } from "./core/format.js";
import { toast } from "./core/toast.js";
import { loadCollectionNames } from "./core/state.js";
import { initTabs } from "./tabs.js";
import { initSetup } from "./setup.js";
import { refreshStatus } from "./status.js";
import { initLibrary, loadLibrary } from "./library.js";
import { initCollections, loadCollections } from "./collections.js";
import { initSearch } from "./search.js";
import { initPlanner } from "./planner.js";
import { initJobs, loadJobs } from "./jobs.js";

const STATUS_REFRESH_MS = 15000;

installImageFallbacks();

// Tabs that show live data reload it every time they are opened.
initTabs({
  library: loadLibrary,
  collections: loadCollections,
  jobs: loadJobs,
});

initLibrary();
initCollections();
initSearch();
initPlanner();
initJobs();

/** Loads everything that needs the osu! folder. Runs at startup and again whenever the folder is (re)chosen. */
function loadEverything() {
  refreshStatus();
  loadCollectionNames().catch(error => {
    if (!error.setupRequired) toast(error.message, true);
  });
  $("nav button.active").click(); // reloads whichever tab is open (Library by default)
}

setInterval(refreshStatus, STATUS_REFRESH_MS);

// Nothing is loaded until the server knows where osu! is; the setup dialog handles asking if it does not.
initSetup(loadEverything).catch(error => toast(error.message, true));
