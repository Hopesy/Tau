namespace Tau.WebUi.Ui;

public static class WebUiPage
{
    public static string Html =>
        """
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1" />
          <title>Tau Web UI</title>
          <style>
            :root { color-scheme: dark; }
            * { box-sizing: border-box; }
            body { margin: 0; font-family: Inter, Segoe UI, sans-serif; background: #0b1020; color: #e8edf7; }
            .shell { display: grid; grid-template-columns: 320px 1fr; min-height: 100vh; }
            aside { border-right: 1px solid rgba(255,255,255,.08); padding: 20px; background: #0e152b; overflow: auto; }
            main { display: grid; grid-template-rows: auto auto 1fr auto; min-height: 100vh; }
            h1 { margin: 0 0 8px; font-size: 22px; }
            h2 { margin: 0 0 12px; font-size: 14px; text-transform: uppercase; letter-spacing: .08em; color: #8fa3c7; }
            .meta { color: #9fb0cf; font-size: 13px; line-height: 1.6; white-space: pre-wrap; }
            .toolbar, .composer, .settings { padding: 16px 20px; border-bottom: 1px solid rgba(255,255,255,.08); }
            .composer { border-top: 1px solid rgba(255,255,255,.08); border-bottom: none; display: flex; gap: 12px; }
            button { background: #3567ff; color: white; border: none; border-radius: 10px; padding: 10px 14px; cursor: pointer; }
            button.secondary { background: #1b2745; }
            select, input, textarea { width: 100%; border-radius: 12px; border: 1px solid rgba(255,255,255,.12); background: #0f1830; color: inherit; padding: 12px; }
            textarea { min-height: 88px; resize: vertical; }
            label { display: grid; gap: 6px; font-size: 12px; color: #8fa3c7; text-transform: uppercase; letter-spacing: .08em; }
            .settings-grid { display: grid; grid-template-columns: 1fr 1fr 1fr auto; gap: 12px; align-items: end; }
            #sessions { display: grid; gap: 10px; margin-top: 16px; }
            .session { padding: 12px; border: 1px solid rgba(255,255,255,.08); border-radius: 12px; background: rgba(255,255,255,.02); cursor: pointer; }
            .session.active { border-color: #3567ff; }
            .session-title { display:flex; justify-content:space-between; gap:8px; }
            .badge { font-size: 11px; padding: 3px 8px; border-radius: 999px; background: rgba(53,103,255,.18); color: #bfd0ff; }
            #messages { padding: 20px; overflow: auto; display: grid; gap: 14px; }
            .message { border: 1px solid rgba(255,255,255,.08); border-radius: 14px; padding: 14px; background: rgba(255,255,255,.03); }
            .message.user { background: rgba(53,103,255,.12); }
            .role { font-size: 12px; text-transform: uppercase; letter-spacing: .08em; color: #8fa3c7; margin-bottom: 8px; }
            .thinking, .tooling, .error { margin-top: 10px; font-size: 13px; color: #9fb0cf; white-space: pre-wrap; }
            .error { color: #ff9da5; }
            .stack { display:grid; gap:12px; }
          </style>
        </head>
        <body>
          <div class="shell">
            <aside>
              <h1>Tau Web UI</h1>
              <div id="status" class="meta">Loading status…</div>
              <div style="margin-top:16px; display:flex; gap:8px;">
                <button id="new-session">New Session</button>
                <button id="refresh" class="secondary">Refresh</button>
              </div>
              <div id="sessions"></div>
            </aside>
            <main>
              <div class="toolbar">
                <h2>Conversation</h2>
                <div class="meta">Second real WebUi slice: runtime-backed chat + persisted sessions + provider/model selection.</div>
              </div>
              <div class="settings stack">
                <div class="settings-grid">
                  <label>Title<input id="session-title" placeholder="Session title" /></label>
                  <label>Provider<select id="provider"></select></label>
                  <label>Model<select id="model"></select></label>
                  <button id="save-settings" class="secondary">Save</button>
                </div>
                <div id="session-meta" class="meta">No session selected.</div>
              </div>
              <div id="messages"></div>
              <div class="composer">
                <textarea id="prompt" placeholder="Ask Tau to inspect code, explain architecture, or run the coding-agent runtime."></textarea>
                <button id="send">Send</button>
              </div>
            </main>
          </div>
          <script>
            let currentSessionId = null;
            let catalog = { providers: [] };

            async function fetchJson(url, options) {
              const response = await fetch(url, options);
              if (!response.ok) throw new Error(await response.text());
              return await response.json();
            }

            function escapeHtml(text) {
              return (text || '').replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;');
            }

            function providerModels(providerId) {
              return catalog.providers.find(p => p.id === providerId)?.models || [];
            }

            function bindModels(providerId, preferredModel) {
              const modelSelect = document.getElementById('model');
              const models = providerModels(providerId);
              modelSelect.innerHTML = models.map(model => `<option value="${model.id}">${escapeHtml(model.name)} (${escapeHtml(model.id)})</option>`).join('');
              if (preferredModel && models.some(model => model.id === preferredModel)) {
                modelSelect.value = preferredModel;
              }
            }

            function applySessionSettings(session) {
              document.getElementById('session-title').value = session?.title || '';
              const providerSelect = document.getElementById('provider');
              providerSelect.value = session?.provider || providerSelect.value;
              bindModels(providerSelect.value, session?.model);
              const persisted = session?.persisted ? 'persisted' : 'memory';
              document.getElementById('session-meta').textContent = session
                ? `provider=${session.provider} | model=${session.model} | messages=${session.messages.length} | storage=${persisted}`
                : 'No session selected.';
            }

            function renderMessages(session) {
              const root = document.getElementById('messages');
              if (!session) {
                root.innerHTML = '<div class="meta">Create a session to start chatting.</div>';
                return;
              }
              root.innerHTML = session.messages.map(message => `
                <div class="message ${message.role}">
                  <div class="role">${message.role}</div>
                  <div>${escapeHtml(message.text || '')}</div>
                  ${message.thinking ? `<div class="thinking">thinking\n${escapeHtml(message.thinking)}</div>` : ''}
                  ${message.toolEvents && message.toolEvents.length ? `<div class="tooling">tools\n${escapeHtml(message.toolEvents.join('\n'))}</div>` : ''}
                  ${message.error ? `<div class="error">error\n${escapeHtml(message.error)}</div>` : ''}
                </div>`).join('');
              root.scrollTop = root.scrollHeight;
            }

            function renderSessions(sessions) {
              const root = document.getElementById('sessions');
              root.innerHTML = sessions.map(session => `
                <div class="session ${session.id === currentSessionId ? 'active' : ''}" data-id="${session.id}">
                  <div class="session-title"><strong>${escapeHtml(session.title)}</strong><span class="badge">${escapeHtml(session.provider)}</span></div>
                  <div class="meta">${escapeHtml(session.model)}\n${session.messages.length} messages</div>
                </div>`).join('');
              root.querySelectorAll('.session').forEach(node => node.addEventListener('click', () => openSession(node.dataset.id)));
            }

            async function loadStatus() {
              const status = await fetchJson('/api/status');
              document.getElementById('status').textContent = `provider=${status.defaultProvider} | model=${status.defaultModel} | sessions=${status.sessionCount} | persisted=${status.persistenceEnabled}\nstore=${status.sessionsPath}`;
            }

            async function loadCatalog() {
              catalog = await fetchJson('/api/catalog');
              const providerSelect = document.getElementById('provider');
              providerSelect.innerHTML = catalog.providers.map(provider => `<option value="${provider.id}">${escapeHtml(provider.id)}</option>`).join('');
              providerSelect.addEventListener('change', () => bindModels(providerSelect.value));
              if (catalog.providers.length) {
                providerSelect.value = catalog.providers[0].id;
                bindModels(providerSelect.value);
              }
            }

            async function loadSessions() {
              const sessions = await fetchJson('/api/sessions');
              renderSessions(sessions);
              if (!currentSessionId && sessions.length) {
                await openSession(sessions[0].id);
              }
            }

            async function openSession(id) {
              currentSessionId = id;
              const session = await fetchJson(`/api/sessions/${id}`);
              renderSessions(await fetchJson('/api/sessions'));
              applySessionSettings(session);
              renderMessages(session);
            }

            async function createSession() {
              const title = document.getElementById('session-title').value.trim() || null;
              const provider = document.getElementById('provider').value;
              const model = document.getElementById('model').value;
              const session = await fetchJson('/api/sessions', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ title, provider, model })
              });
              currentSessionId = session.id;
              await loadStatus();
              await loadSessions();
              applySessionSettings(session);
              renderMessages(session);
            }

            async function saveSettings() {
              if (!currentSessionId) return;
              const session = await fetchJson(`/api/sessions/${currentSessionId}`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                  title: document.getElementById('session-title').value.trim(),
                  provider: document.getElementById('provider').value,
                  model: document.getElementById('model').value
                })
              });
              await loadStatus();
              await loadSessions();
              applySessionSettings(session);
            }

            async function sendMessage() {
              if (!currentSessionId) await createSession();
              const prompt = document.getElementById('prompt');
              const text = prompt.value.trim();
              if (!text) return;
              prompt.value = '';
              const session = await fetchJson(`/api/sessions/${currentSessionId}/messages`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ text })
              });
              await loadStatus();
              renderMessages(session);
              applySessionSettings(session);
              await loadSessions();
            }

            document.getElementById('new-session').addEventListener('click', createSession);
            document.getElementById('refresh').addEventListener('click', async () => { await loadStatus(); await loadCatalog(); await loadSessions(); });
            document.getElementById('save-settings').addEventListener('click', saveSettings);
            document.getElementById('send').addEventListener('click', sendMessage);
            document.getElementById('prompt').addEventListener('keydown', event => {
              if ((event.ctrlKey || event.metaKey) && event.key === 'Enter') sendMessage();
            });
            loadStatus().then(loadCatalog).then(loadSessions);
          </script>
        </body>
        </html>
        """;
}
