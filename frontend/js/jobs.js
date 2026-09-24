// Jobs tab: background work (downloads, plan generation) plus the progress widgets other tabs reuse.

import { $, esc } from "./core/dom.js";
import { api } from "./core/api.js";
import { toast, guard } from "./core/toast.js";
import { loadCollectionNames } from "./core/state.js";
import { refreshStatus } from "./status.js";

const MAX_JOBS_SHOWN = 10;
const INLINE_POLL_MS = 1000;
const BADGE_POLL_MS = 1500;

/** Jobs started from this page (id -> what to run when it completes), so the UI refreshes once they finish. */
const watchedJobs = new Map();

function renderJob(job) {
  const percent = job.total
    ? Math.round(100 * job.done / job.total)
    : (job.state === "Running" ? 5 : 100);

  const progressText = job.total ? `<span class="muted small">${job.done}/${job.total}</span>` : "";
  const stateControl = job.state === "Running"
    ? `<button class="link danger" data-cancel="${job.id}">Cancel</button>`
    : `<span class="small">${job.state}</span>`;
  const log = job.log
    ? `<div class="log">${esc([...job.log].reverse().join("\n"))}</div>` // newest line first
    : "";

  return `
    <div class="row toolbar job-header">
      <b>${esc(job.kind)}</b><span class="muted">${esc(job.status)}</span><span class="spacer"></span>
      ${progressText}
      ${stateControl}
    </div>
    <div class="progress"><div style="width:${percent}%"></div></div>
    ${log}`;
}

/**
 * Polls one job once a second and draws it inside `targetSelector`.
 * `onDone` is called with the finished job when it completes successfully.
 */
export function trackInline(jobId, targetSelector, onDone) {
  const target = $(targetSelector);

  async function poll() {
    try {
      const job = await api("/jobs/" + jobId);
      target.innerHTML = `<div class="job">${renderJob(job)}</div>`;

      if (job.state === "Running") {
        setTimeout(poll, INLINE_POLL_MS);
      } else if (job.state === "Completed") {
        onDone?.(job);
      } else {
        toast(job.error || job.state, true);
      }
    } catch (error) {
      toast(error.message, true);
    }
  }
  poll();
}

/**
 * Starts tracking a job in the header badge. When it stops running, the status line and collection lists are
 * refreshed, and `onCompleted` (optional) runs if it finished successfully.
 */
export function watchJob(jobId, onCompleted) {
  watchedJobs.set(jobId, onCompleted);
  updateJobBadge();
}

async function updateJobBadge() {
  try {
    const jobs = await api("/jobs");
    const runningIds = new Set(jobs.filter(job => job.state === "Running").map(job => job.id));

    $("#jobBadge").textContent = runningIds.size;
    $("#jobBadge").classList.toggle("hidden", !runningIds.size);
    if ($("#tab-jobs").classList.contains("active")) loadJobs();
    if (runningIds.size) setTimeout(updateJobBadge, BADGE_POLL_MS);

    const finished = [...watchedJobs].filter(([id]) => !runningIds.has(id));
    if (finished.length === 0) return;

    for (const [id, onCompleted] of finished) {
      watchedJobs.delete(id);
      if (jobs.find(job => job.id === id)?.state === "Completed") onCompleted?.();
    }
    refreshStatus();
    loadCollectionNames();
  } catch {
    // The server is probably restarting; the next user action will retry.
  }
}

export const loadJobs = guard(async () => {
  const jobs = await api("/jobs");
  const details = await Promise.all(jobs.slice(0, MAX_JOBS_SHOWN).map(job => api("/jobs/" + job.id)));

  $("#jobList").innerHTML = details.map(job => `<div class="job">${renderJob(job)}</div>`).join("")
    || `<p class="muted">No jobs yet.</p>`;
});

export function initJobs() {
  // Cancel buttons can appear both in the Jobs tab and inside inline progress boxes.
  document.addEventListener("click", guard(async event => {
    const jobId = event.target.dataset?.cancel;
    if (!jobId) return;

    await api(`/jobs/${jobId}/cancel`, { method: "POST" });
    toast("Cancelling…");
  }));
}
