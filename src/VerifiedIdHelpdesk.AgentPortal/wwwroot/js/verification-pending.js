// ── Pending page — reads config from data attributes on #pendingData ──
const dataEl = document.getElementById('pendingData');
const sessionId = dataEl.dataset.sessionId;
const apiBaseUrl = dataEl.dataset.apiBaseUrl;
const hubPath = dataEl.dataset.hubPath;
const verifyPortalUrl = dataEl.dataset.verifyPortalUrl || '';
const expiresAt = new Date(dataEl.dataset.expiresAt);
let pollingInterval;

// ── Countdown timer ──────────────────────────────────────────────────
function updateCountdown () {
    const remaining = Math.max(0, Math.floor((expiresAt - Date.now()) / 1000));
    const mins = Math.floor(remaining / 60);
    const secs = remaining % 60;
    const el = document.getElementById('countdown');
    el.textContent = remaining > 0
        ? `Expires in ${mins}:${secs.toString().padStart(2, '0')}`
        : 'Code expired';
    if (remaining <= 60) el.classList.add('expiring-soon');
    if (remaining === 0) {
        clearInterval(countdownInterval);
        document.getElementById('expiredActions').style.display = 'block';
    }
}
const countdownInterval = setInterval(updateCountdown, 1000);
updateCountdown();

// ── SignalR connection ───────────────────────────────────────────────
try {
    const connection = new signalR.HubConnectionBuilder()
        .withUrl(`${apiBaseUrl}${hubPath}`)
        .withAutomaticReconnect()
        .build();

    connection.on('VerificationComplete', (data) => {
        clearInterval(pollingInterval);
        showVerified(data);
    });

    connection.on('VerificationFailed', () => {
        showStatus('error', 'Verification failed. Ask the caller to try again.');
    });

    connection.start().then(() => {
        connection.invoke('JoinSession', sessionId);
    }).catch(() => {
        startPolling();
    });
} catch (e) {
    startPolling();
}

// ── Polling fallback ─────────────────────────────────────────────────
function startPolling () {
    if (pollingInterval) return;
    pollingInterval = setInterval(pollStatus, 3000);
}

async function pollStatus () {
    try {
        const resp = await fetch(`${apiBaseUrl}/api/verification/public-status/${sessionId}`);
        if (!resp.ok) return;
        const data = await resp.json();
        if (data.status === 'verified') {
            clearInterval(pollingInterval);
            showVerified({
                callerName: 'Identity verified',
                employeeId: '',
                department: '',
                verifiedAt: data.verifiedAt
            });
        } else if (data.status === 'failed' || data.status === 'expired') {
            clearInterval(pollingInterval);
            showStatus('error', `Session ${data.status}. Please generate a new code.`);
        }
    } catch (e) { /* silently ignore network errors */ }
}

function showVerified (data) {
    document.getElementById('verificationCode').style.display = 'none';
    document.getElementById('countdown').style.display = 'none';
    document.getElementById('resultPanel').style.display = 'block';
    document.getElementById('resultName').textContent = `\u2713 ${data.callerName}`;

    const hasDetails = Boolean(data.employeeId || data.department);
    document.getElementById('resultMeta').textContent = hasDetails
        ? `Employee ID: ${data.employeeId || '\u2014'}  \u00b7  Department: ${data.department || '\u2014'}`
        : 'Verification complete. Loading caller details...';

    setTimeout(() => {
        window.location.href = `/Verification/Result/${sessionId}`;
    }, 1500);
}

function showStatus (type, message) {
    const el = document.getElementById('statusMessage');
    el.className = `alert alert-${type === 'error' ? 'error' : 'info'}`;
    el.textContent = message;
    el.style.display = 'block';

    if (type === 'error') {
        document.getElementById('expiredActions').style.display = 'block';
    }
}

// ── Copy helpers ─────────────────────────────────────────────────────
function buildCombinedMessage () {
    const code = document.getElementById('verificationCode')?.textContent?.trim();
    if (!code && !verifyPortalUrl) return '';

    if (code && verifyPortalUrl) {
        return `Open the verification portal: ${verifyPortalUrl}\nEnter your email address and this code: ${code}`;
    }

    return code || verifyPortalUrl;
}

function attachCopyButton (buttonId, getValue, defaultLabel) {
    const button = document.getElementById(buttonId);
    if (!button) return;

    button.addEventListener('click', function () {
        const value = getValue();
        if (!value) return;

        navigator.clipboard.writeText(value).then(() => {
            this.textContent = '✓ Copied';
            setTimeout(() => { this.textContent = defaultLabel; }, 2000);
        }).catch(() => {
            this.textContent = '✗ Failed';
            setTimeout(() => { this.textContent = defaultLabel; }, 2000);
        });
    });
}

attachCopyButton('copyCodeBtn', () => document.getElementById('verificationCode')?.textContent?.trim(), '📋 Copy Code');
attachCopyButton('copyCombinedBtn', buildCombinedMessage, '📝 Copy Code + Link');
attachCopyButton('copyPortalLinkBtn', () => verifyPortalUrl, '🔗 Copy Link');
