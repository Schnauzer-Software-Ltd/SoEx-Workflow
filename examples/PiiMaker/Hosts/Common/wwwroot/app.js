'use strict';

// One static page drives any running host. Pick a host from the dropdown; every call is prefixed with its
// base URL (CORS is open on each host). The trigger controller exposes IMembershipManager as a single POST
// /IMembershipManager/Trigger; the body is a polymorphic TriggerBase whose $type names the trigger. The
// /example/* endpoints are hand-written affordances (scenario toggles, status, erasure).

const hostSel = document.getElementById('host');
const healthDot = document.getElementById('health');
const runtimeLbl = document.getElementById('runtime');
const instancesEl = document.getElementById('instances');
const logEl = document.getElementById('log');

let base = hostSel.value;
let caps = {};
const tracked = new Map();   // instanceId -> { flow, done }

// The selected host reports its backend dashboard (if any) in /example/host as { port, https }. Build the
// link relative to wherever this page was loaded from, so it works behind a proxy at the VM's address.
// Runtimes with no server dashboard (InProc in-process, Elsa a SQLite file) report none and show no link.
function updateDashboardLink() {
  const dash = document.getElementById('dash');
  const d = caps.dashboard;
  if (d && d.port) {
    dash.href = `${d.https ? 'https' : 'http'}://${location.hostname}:${d.port}`;
    dash.textContent = `${caps.runtime} dashboard ↗`;
    dash.hidden = false;
  } else {
    dash.hidden = true;
  }
}

// ---- transport -------------------------------------------------------------------------------
const entryUrl = (method) => `${base}/IMembershipManager/${method}`;

async function post(url, body) {
  const res = await fetch(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!res.ok) throw new Error(`${res.status} ${res.statusText} — ${await res.text()}`);
  const txt = await res.text();
  return txt ? JSON.parse(txt) : null;
}
// One polymorphic POST /IMembershipManager/Trigger; the $type names the trigger case and must be the first
// property in the body (System.Text.Json reads the discriminator first).
const TRIGGER_TYPE = 'PiiMaker.Manager.Membership.Interface.TriggerBase+';
const trigger = (kind, fields = {}) => post(entryUrl('Trigger'), { $type: TRIGGER_TYPE + kind, ...fields });

// ---- logging ---------------------------------------------------------------------------------
function log(msg, cls) {
  const t = new Date().toLocaleTimeString();
  const line = document.createElement('div');
  if (cls) line.className = cls;
  line.textContent = `${t}  ${msg}`;
  logEl.prepend(line);
}

// ---- instance tracking -----------------------------------------------------------------------
function track(flow, id) {
  tracked.set(id, { flow, done: false });
  renderInstances();
}
function renderInstances() {
  if (tracked.size === 0) {
    instancesEl.innerHTML = '<div class="empty">No instances yet — start a flow.</div>';
    return;
  }
  instancesEl.innerHTML = '';
  for (const [id, s] of [...tracked].reverse()) {
    const row = document.createElement('div');
    row.className = 'inst';
    row.innerHTML = `<code title="${id}">${s.flow}: ${id}</code>` +
      (s.done ? '<span class="tag done">shredded</span>' : '<span class="tag run">running</span>');
    instancesEl.appendChild(row);
  }
}

// ---- host capabilities -----------------------------------------------------------------------
async function loadHost() {
  base = hostSel.value;
  try {
    const res = await fetch(`${base}/example/host`);
    caps = await res.json();
    healthDot.classList.add('up');
    runtimeLbl.textContent = `${caps.runtime} · online`;
    log(`connected to ${caps.runtime} (${base})`, 'ok');
  } catch (e) {
    caps = {};
    healthDot.classList.remove('up');
    runtimeLbl.textContent = 'offline';
    log(`cannot reach ${base} — is that host running?`, 'err');
  }
  const online = !!caps.runtime;
  for (const card of document.querySelectorAll('.card')) {
    const cap = card.dataset.cap;
    const usable = online && (cap === 'always' || !!caps[cap]);
    card.classList.toggle('disabled', !usable);
    // Explain only when the host is up but doesn't wire this flow — not when it's merely offline.
    card.classList.toggle('show-note', online && !usable);
    for (const el of card.querySelectorAll('input, button')) el.disabled = !usable;
  }
  updateDashboardLink();
}

// ---- actions ---------------------------------------------------------------------------------
const v = (id) => document.getElementById(id).value.trim();

const actions = {
  async startOnboarding() {
    const id = await trigger('StartOnboarding', { orgId: v('on-org'), email: v('on-email'), offer: v('on-offer') });
    track('onboard', id); log(`began onboarding → ${id}`, 'ok');
  },
  async accountVerified() {
    await trigger('AccountVerified', { orgId: v('on-org'), email: v('on-email') });
    log('raised account-verified', 'ok');
  },
  async inviteAccepted() {
    await trigger('InviteAccepted', { orgId: v('on-org'), email: v('on-email') });
    log('raised invite-accepted', 'ok');
  },
  async startRenewal() {
    const id = await trigger('StartRenewal', { subscriberId: v('sub-id') });
    track('renew', id); log(`began renewal → ${id}`, 'ok');
  },
  async paymentUpdated() {
    await trigger('PaymentUpdated', { subscriberId: v('sub-id') });
    log('raised payment-updated', 'ok');
  },
  async startOffboarding() {
    const id = await trigger('StartOffboarding', { subjectId: v('off-id') });
    track('offboard', id); log(`began offboarding → ${id}`, 'ok');
  },
  async forceDecline() {
    await post(`${base}/example/billing/decline`, { subject: v('sub-id'), period: 1 });
    log(`forced decline for ${v('sub-id')} period 1`, 'ok');
  },
  async clearDecline() {
    await post(`${base}/example/billing/clear`, { subject: v('sub-id'), period: 1 });
    log('cleared decline', 'ok');
  },
  async erase() {
    const r = await post(`${base}/example/erase`, { subject: v('er-id') });
    log(`erasure request ${r.requestId} admitted; drained ${r.drained} request(s)`, 'ok');
  },
  async restart() {
    await post(`${base}/example/restart-host`);
    log('host restarted — durable instances resume on the fresh process', 'ok');
  },
};

document.addEventListener('click', async (e) => {
  const btn = e.target.closest('button[data-act]');
  if (!btn) return;
  btn.disabled = true;
  try { await actions[btn.dataset.act](); }
  catch (err) { log(`${btn.dataset.act} failed — ${err.message}`, 'err'); }
  finally { btn.disabled = false; }
});

hostSel.addEventListener('change', loadHost);

// Make the host dropdown origin-aware: the page may be served through a proxy at another host/IP, where
// "localhost" would mean the visitor's own machine rather than where the hosts run. Point each option at
// the hostname the page was loaded from (unchanged when that is localhost).
for (const opt of hostSel.options) opt.value = opt.value.replace('//localhost:', `//${location.hostname}:`);

// ---- polling: instance shred status + dunning attempts ---------------------------------------
async function poll() {
  for (const [id, s] of tracked) {
    if (s.done) continue;
    try {
      const res = await fetch(`${base}/example/status/${encodeURIComponent(id)}`);
      const st = await res.json();
      if (!st.keyLive) { s.done = true; renderInstances(); log(`${id} completed — key shredded`, 'ok'); }
    } catch { /* host may be momentarily down (e.g. restart) */ }
  }
  if (caps.renewal) {
    try {
      const res = await fetch(`${base}/example/billing/attempts?subject=${encodeURIComponent(v('sub-id'))}&period=1`);
      const a = await res.json();
      document.getElementById('sub-attempts').textContent = a.attempts;
    } catch { /* ignore */ }
  }
}

setInterval(poll, 1000);
loadHost();
