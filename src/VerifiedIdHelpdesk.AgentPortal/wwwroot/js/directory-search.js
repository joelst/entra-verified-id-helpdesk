// ── Directory search typeahead ────────────────────────────────────────
let searchTimeout;
let abortController;
const searchCache = new Map();

document.getElementById('callerSearch').addEventListener('input', function () {
    clearTimeout(searchTimeout);
    const query = this.value.trim();
    if (query.length < 2) {
        hideResults();
        return;
    }
    searchTimeout = setTimeout(() => searchDirectory(query), 350);
});

async function searchDirectory(query) {
    if (searchCache.has(query)) {
        renderResults(searchCache.get(query));
        return;
    }

    if (abortController) abortController.abort();
    abortController = new AbortController();

    try {
        const container = document.getElementById('searchResults');
        container.innerHTML = '<div class="search-result-item" style="color:var(--color-text-muted);cursor:default;"><span class="spinner"></span> Searching...</div>';
        container.style.display = 'block';

        const resp = await fetch(`/api/directory/search?q=${encodeURIComponent(query)}`, {
            headers: { 'X-Requested-With': 'XMLHttpRequest' },
            signal: abortController.signal
        });
        if (!resp.ok) {
            if (resp.status === 429) {
                renderNoResults('Too many searches — please wait a moment and try again.');
                return;
            }
            console.error('Directory search returned', resp.status);
            renderNoResults('Directory search unavailable. You can enter details manually below.');
            return;
        }
        const results = await resp.json();
        searchCache.set(query, results);
        if (searchCache.size > 50) searchCache.delete(searchCache.keys().next().value);
        renderResults(results);
    } catch (e) {
        if (e.name === 'AbortError') return;
        console.error('Directory search failed:', e);
        renderNoResults('Directory search unavailable. You can enter details manually below.');
    }
}

function renderResults(results) {
    const container = document.getElementById('searchResults');
    if (!results || results.length === 0) {
        hideResults();
        return;
    }
    container.innerHTML = results.map(u => `
        <div class="search-result-item"
             data-id="${esc(u.entraId)}"
             data-email="${esc(u.email)}"
             data-name="${esc(u.displayName)}">
            <div class="search-result-name">${esc(u.displayName)}</div>
            <div class="search-result-meta">${esc(u.email)} \u00b7 ${esc(u.department || u.jobTitle || '')}</div>
        </div>
    `).join('');
    container.style.display = 'block';

    container.querySelectorAll('.search-result-item').forEach(item => {
        item.addEventListener('click', () => selectCaller(
            item.dataset.id, item.dataset.email, item.dataset.name));
    });
}

function selectCaller(entraId, email, name) {
    document.getElementById('callerEntraId').value = entraId;
    document.getElementById('callerEmail').value = email;
    document.getElementById('callerDisplayName').value = name;
    document.getElementById('callerSearch').value = name;
    hideResults();

    const info = document.getElementById('selectedCaller');
    info.textContent = `\u2713 Selected: ${name} (${email})`;
    info.style.display = 'block';
    document.getElementById('submitBtn').disabled = false;
}

function hideResults() {
    document.getElementById('searchResults').style.display = 'none';
}

function renderNoResults(message) {
    const container = document.getElementById('searchResults');
    container.innerHTML = `<div class="search-result-item" style="color:var(--color-text-muted);cursor:default;">${esc(message)}</div>`;
    container.style.display = 'block';
    document.getElementById('manualEntry').style.display = 'block';
}

// Manual email entry — enables the submit button when a valid email is typed
document.getElementById('manualEmail').addEventListener('input', function () {
    const email = this.value.trim();
    if (email && email.includes('@')) {
        const name = document.getElementById('callerSearch').value.trim() || email.split('@')[0];
        document.getElementById('callerEmail').value = email;
        document.getElementById('callerDisplayName').value = name;
        document.getElementById('callerEntraId').value = 'manual-entry';
        document.getElementById('submitBtn').disabled = false;
    } else {
        document.getElementById('submitBtn').disabled = true;
    }
});

function esc(str) {
    if (!str) return '';
    return str.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
}

// Close dropdown on outside click
document.addEventListener('click', (e) => {
    if (!e.target.closest('#callerSearch') && !e.target.closest('#searchResults'))
        hideResults();
});

// ── Form submission loading state ────────────────────────────────────
document.querySelector('form[method="post"]')?.addEventListener('submit', function () {
    const btn = document.getElementById('submitBtn');
    if (btn && !btn.disabled) {
        btn.disabled = true;
        btn.innerHTML = '<span class="spinner"></span> Sending...';
    }
});
