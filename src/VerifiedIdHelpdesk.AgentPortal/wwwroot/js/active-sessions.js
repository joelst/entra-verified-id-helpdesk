// ── Active sessions panel on Create page ──────────────────────────────
(function () {
    const configEl = document.getElementById('sessionConfig');
    if (!configEl) return;

    const maxSessions = 3;

    async function loadSessions() {
        try {
            const resp = await fetch('/Verification/PendingSessions');
            if (!resp.ok) return;
            const sessions = await resp.json();
            render(sessions);
        } catch (e) { /* silently ignore */ }
    }

    function render(sessions) {
        const container = document.getElementById('activeSessions');
        const cards = document.getElementById('sessionCards');
        const badge = document.getElementById('sessionCount');
        const submitBtn = document.getElementById('submitBtn');

        if (!sessions || sessions.length === 0) {
            container.style.display = 'none';
            if (submitBtn && !submitBtn.dataset.callerLock) {
                submitBtn.disabled = false;
            }
            return;
        }

        container.style.display = 'block';
        badge.textContent = sessions.length + '/' + maxSessions + ' active';

        cards.innerHTML = sessions.map(function (s) {
            var remaining = Math.max(0, Math.floor((new Date(s.expiresAt) - Date.now()) / 1000));
            var mins = Math.floor(remaining / 60);
            var secs = remaining % 60;
            var timeStr = remaining > 0
                ? mins + ':' + secs.toString().padStart(2, '0') + ' remaining'
                : 'Expired';
            var channelIcons = { email: '📧', teams: '💬', verbal: '🗣️', sms: '📱' };
            var channelIcon = channelIcons[s.deliveryChannel] || '📧';

            return '<a href="/Verification/Pending?sessionId=' + esc(s.sessionId)
                + '&displayCode=••••••••'
                + '&callerDisplayName=' + encodeURIComponent(s.callerDisplayName)
                + '&deliveryChannel=' + esc(s.deliveryChannel)
                + '&expiresAt=' + encodeURIComponent(s.expiresAt)
                + '" class="session-card">'
                + '<div class="session-card-name">' + esc(s.callerDisplayName) + '</div>'
                + '<div class="session-card-meta">'
                + channelIcon + ' ' + esc(s.deliveryChannel) + ' · ' + timeStr
                + (s.ticketId ? ' · ' + esc(s.ticketId) : '')
                + '</div></a>';
        }).join('');

        // Disable submit if at max concurrent sessions
        if (sessions.length >= maxSessions && submitBtn) {
            submitBtn.disabled = true;
            submitBtn.title = 'Maximum ' + maxSessions + ' concurrent verifications reached';
        }
    }

    function esc(str) {
        if (!str) return '';
        return str.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    }

    // Load immediately and poll every 15 seconds
    loadSessions();
    setInterval(loadSessions, 15000);
})();
