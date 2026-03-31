// ── Present page — reads config from data attributes on #presentData ──
const presentEl = document.getElementById('presentData');
const sessionId = presentEl.dataset.sessionId;
const apiBase = presentEl.dataset.apiBaseUrl;
const expiresAt = new Date(presentEl.dataset.expiresAt);

function updateCountdown() {
    const remaining = Math.max(0, Math.floor((expiresAt - Date.now()) / 1000));
    const mins = Math.floor(remaining / 60);
    const secs = remaining % 60;
    const el = document.getElementById('countdown');
    el.textContent = remaining > 0
        ? `This request expires in ${mins}:${secs.toString().padStart(2, '0')}`
        : 'This request has expired. Please start over.';
    if (remaining <= 60) el.classList.add('expiring-soon');
    if (remaining <= 0) { clearInterval(countdownTimer); clearInterval(pollTimer); }
}
const countdownTimer = setInterval(updateCountdown, 1000);
updateCountdown();

const pollTimer = setInterval(async () => {
    try {
        const resp = await fetch(`${apiBase}/api/verification/public-status/${sessionId}`);
        if (!resp.ok) return;
        const data = await resp.json();
        if (data.status === 'verified') {
            clearInterval(pollTimer);
            window.location.href = '/Complete';
        } else if (data.status === 'failed' || data.status === 'expired') {
            clearInterval(pollTimer);
            document.getElementById('statusMsg').className = 'alert alert-error';
            document.getElementById('statusMsg').textContent =
                'This request has expired or failed. Please contact your helpdesk agent.';
            document.getElementById('statusMsg').style.display = 'block';
        }
    } catch (e) { /* ignore transient errors */ }
}, 3000);
