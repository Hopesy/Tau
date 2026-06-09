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
            main { display: grid; grid-template-rows: auto auto 1fr auto; min-height: 100vh; min-width: 0; }
            h1 { margin: 0 0 8px; font-size: 22px; }
            h2 { margin: 0 0 12px; font-size: 14px; text-transform: uppercase; letter-spacing: .08em; color: #8fa3c7; }
            .meta { color: #9fb0cf; font-size: 13px; line-height: 1.6; white-space: pre-wrap; }
            .toolbar, .composer, .settings { padding: 16px 20px; border-bottom: 1px solid rgba(255,255,255,.08); }
            .composer { border-top: 1px solid rgba(255,255,255,.08); border-bottom: none; display: grid; gap: 10px; }
            .composer-row { display: grid; grid-template-columns: 1fr auto; gap: 12px; align-items: stretch; }
            .composer-actions { display: grid; gap: 8px; align-content: start; }
            .sidebar-actions { margin-top: 16px; display: flex; gap: 8px; flex-wrap: wrap; }
            .import-toggle { margin-top: 12px; display: flex; align-items: center; gap: 8px; width: fit-content; color: #bfd0ff; text-transform: none; letter-spacing: 0; cursor: pointer; }
            .import-toggle input { width: 14px; height: 14px; margin: 0; padding: 0; border: none; background: transparent; accent-color: #3567ff; flex: 0 0 auto; }
            .import-toggle span { color: inherit; }
            button { background: #3567ff; color: white; border: none; border-radius: 10px; padding: 10px 14px; cursor: pointer; }
            button.secondary { background: #1b2745; }
            button:disabled { opacity: .55; cursor: default; }
            select, input, textarea { width: 100%; border-radius: 12px; border: 1px solid rgba(255,255,255,.12); background: #0f1830; color: inherit; padding: 12px; }
            textarea { min-height: 88px; resize: vertical; }
            label { display: grid; gap: 6px; font-size: 12px; color: #8fa3c7; text-transform: uppercase; letter-spacing: .08em; }
            .settings-grid { display: grid; grid-template-columns: 1fr 1fr 1fr auto; gap: 12px; align-items: end; }
            #sessions { display: grid; gap: 10px; margin-top: 16px; }
            .session { padding: 12px; border: 1px solid rgba(255,255,255,.08); border-radius: 12px; background: rgba(255,255,255,.02); cursor: pointer; }
            .session.active { border-color: #3567ff; }
            .session-title { display:flex; justify-content:space-between; gap:8px; }
            .session-actions { display:flex; gap:6px; margin-top:10px; }
            .session-actions button { padding:6px 8px; border-radius:8px; font-size:12px; }
            .badge { font-size: 11px; padding: 3px 8px; border-radius: 999px; background: rgba(53,103,255,.18); color: #bfd0ff; }
            .conversation-grid { min-height: 0; display: grid; grid-template-columns: minmax(0, 1fr) minmax(280px, 34%); }
            #messages { padding: 20px; overflow: auto; display: grid; align-content: start; gap: 14px; min-width: 0; }
            #artifacts-pane { border-left: 1px solid rgba(255,255,255,.08); background: #091225; min-width: 0; display: grid; grid-template-rows: auto auto 1fr; }
            .artifacts-head { padding: 14px 16px; border-bottom: 1px solid rgba(255,255,255,.08); display: flex; justify-content: space-between; align-items: center; gap: 10px; }
            .artifacts-head h2 { margin: 0; }
            .artifacts-head button { padding: 7px 9px; border-radius: 8px; font-size: 12px; }
            #artifacts-list { padding: 12px; display: flex; flex-wrap: wrap; gap: 8px; border-bottom: 1px solid rgba(255,255,255,.08); min-height: 54px; align-content: start; }
            .artifact-pill { background: #172341; color: #cbd9f3; border: 1px solid rgba(255,255,255,.1); border-radius: 999px; padding: 7px 10px; max-width: 100%; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
            .artifact-pill.active { background: #3567ff; color: #fff; border-color: transparent; }
            #artifact-preview { min-height: 0; overflow: auto; padding: 12px; }
            .artifact-empty { color: #8fa3c7; font-size: 13px; line-height: 1.5; }
            .artifact-frame { width: 100%; min-height: 420px; height: calc(100vh - 300px); border: 1px solid rgba(255,255,255,.1); border-radius: 10px; background: #fff; }
            .artifact-code { margin: 0; padding: 12px; border-radius: 10px; overflow: auto; background: #050b17; border: 1px solid rgba(255,255,255,.08); color: #dce6fa; white-space: pre; }
            .artifact-image { max-width: 100%; border-radius: 10px; border: 1px solid rgba(255,255,255,.1); background: #050b17; }
            .artifact-viewer { display: grid; gap: 12px; min-height: 100%; align-content: start; }
            .artifact-viewer-head { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 10px; color: #9fb0cf; font-size: 12px; }
            .artifact-viewer-title { color: #e8edf7; font-weight: 650; overflow-wrap: anywhere; }
            .artifact-markdown { padding: 12px; border-radius: 10px; border: 1px solid rgba(255,255,255,.08); background: rgba(255,255,255,.03); }
            .artifact-download-link { color: #bfd0ff; text-decoration: none; border: 1px solid rgba(158,183,255,.35); border-radius: 999px; padding: 6px 10px; background: rgba(53,103,255,.12); }
            .artifact-generic { min-height: 260px; display: grid; place-items: center; text-align: center; color: #9fb0cf; }
            .artifact-generic-card { max-width: 420px; display: grid; gap: 10px; padding: 22px; border-radius: 12px; border: 1px solid rgba(255,255,255,.09); background: rgba(255,255,255,.03); }
            .message { border: 1px solid rgba(255,255,255,.08); border-radius: 14px; padding: 14px; background: rgba(255,255,255,.03); }
            .message.user { background: rgba(53,103,255,.12); }
            .role { font-size: 12px; text-transform: uppercase; letter-spacing: .08em; color: #8fa3c7; margin-bottom: 8px; }
            .message-text { line-height: 1.55; white-space: pre-wrap; overflow-wrap: anywhere; }
            .message-text a { color: #9eb7ff; text-decoration: none; border-bottom: 1px solid rgba(158,183,255,.35); }
            .message-text code.inline-code { padding: 1px 5px; border-radius: 6px; background: rgba(255,255,255,.08); color: #d9e4ff; }
            .message-text pre { margin: 10px 0 0; padding: 12px; border-radius: 10px; overflow: auto; background: #071020; border: 1px solid rgba(255,255,255,.08); white-space: pre; }
            .message-text pre code { font-family: Cascadia Code, Consolas, monospace; font-size: 13px; }
            .message-text h1, .message-text h2, .message-text h3, .message-text h4, .message-text h5, .message-text h6 { margin: 12px 0 6px; color:#f3f6ff; letter-spacing:0; text-transform:none; }
            .message-text h1 { font-size: 22px; }
            .message-text h2 { font-size: 18px; }
            .message-text h3 { font-size: 16px; }
            .message-text h4, .message-text h5, .message-text h6 { font-size: 14px; }
            .message-text ul, .message-text ol { margin: 8px 0 0; padding-left: 22px; }
            .message-text li { margin: 4px 0; }
            .message-text blockquote { margin: 10px 0 0; padding: 6px 10px; border-left: 3px solid rgba(158,183,255,.45); background: rgba(158,183,255,.06); color:#c7d4ee; }
            .message-text .table-wrap { margin-top:10px; overflow:auto; border:1px solid rgba(255,255,255,.08); border-radius:10px; }
            .message-text table { width:100%; border-collapse:collapse; min-width:420px; }
            .message-text th, .message-text td { padding:8px 10px; border-bottom:1px solid rgba(255,255,255,.07); text-align:left; vertical-align:top; }
            .message-text th { color:#dce6fa; background:rgba(255,255,255,.04); font-weight:650; }
            .message-text tr:last-child td { border-bottom:none; }
            .task-check { width:14px; height:14px; vertical-align:-2px; margin-right:7px; accent-color:#9eb7ff; }
            .code-label { display: block; margin-bottom: 8px; font-size: 11px; color: #8fa3c7; text-transform: uppercase; letter-spacing: .08em; }
            .thinking, .tooling, .error { margin-top: 10px; font-size: 13px; color: #9fb0cf; }
            .thinking details, .tool-card { border:1px solid rgba(255,255,255,.09); border-radius:10px; background:rgba(7,16,32,.55); }
            .thinking summary { cursor:pointer; padding:8px 10px; color:#b8c7e3; }
            .thinking-content { padding:0 10px 10px; white-space:pre-wrap; color:#9fb0cf; }
            .tooling { display:grid; gap:8px; }
            .tooling-title { color:#8fa3c7; text-transform:uppercase; letter-spacing:.08em; font-size:11px; }
            .tool-card { padding:10px; display:grid; gap:8px; }
            .tool-head { display:flex; flex-wrap:wrap; align-items:center; gap:8px; }
            .tool-name { color:#e8edf7; font-weight:650; }
            .tool-id { color:#6f819f; font-family:Cascadia Code, Consolas, monospace; font-size:11px; }
            .tool-status { border-radius:999px; padding:2px 7px; font-size:11px; text-transform:uppercase; letter-spacing:.06em; background:rgba(158,183,255,.13); color:#bfd0ff; }
            .tool-status.error { background:rgba(255,157,165,.14); color:#ffb9bf; }
            .tool-status.completed { background:rgba(84,211,149,.13); color:#a7f1c9; }
            .tool-status.running, .tool-status.preparing { background:rgba(255,210,102,.12); color:#ffe09a; }
            .tool-card details { border-top:1px solid rgba(255,255,255,.07); padding-top:7px; }
            .tool-card summary { cursor:pointer; color:#9fb0cf; }
            .tool-card pre, .tooling > pre { margin:8px 0 0; padding:10px; border-radius:8px; overflow:auto; background:#050b17; border:1px solid rgba(255,255,255,.08); white-space:pre; }
            .tool-card code, .tooling code { font-family:Cascadia Code, Consolas, monospace; font-size:12px; }
            .error { color: #ff9da5; }
            .stack { display:grid; gap:12px; }
            .attachments { display:flex; flex-wrap:wrap; gap:8px; margin-top:10px; }
            .attachment { display:grid; grid-template-columns:auto 1fr auto; align-items:center; gap:8px; max-width:320px; border:1px solid rgba(255,255,255,.1); border-radius:10px; background:rgba(255,255,255,.04); padding:7px 8px; font-size:12px; color:#c4d1ea; }
            .attachment img { width:34px; height:34px; object-fit:cover; border-radius:7px; background:#071020; }
            .attachment-icon { display:grid; place-items:center; width:34px; height:34px; border-radius:7px; background:#101c38; color:#9eb7ff; font-weight:700; }
            .attachment-name { overflow:hidden; text-overflow:ellipsis; white-space:nowrap; color:#e8edf7; }
            .attachment-meta { overflow:hidden; text-overflow:ellipsis; white-space:nowrap; color:#8fa3c7; }
            .attachment-remove { background:transparent; color:#9fb0cf; border:1px solid rgba(255,255,255,.12); border-radius:8px; padding:4px 7px; }
            @media (max-width: 820px) {
              .shell { grid-template-columns: 1fr; }
              aside { border-right: none; border-bottom: 1px solid rgba(255,255,255,.08); max-height: 38vh; }
              .settings-grid { grid-template-columns: 1fr; }
              .composer-row { grid-template-columns: 1fr; }
              .composer-actions { grid-template-columns: 1fr 1fr; }
              .conversation-grid { grid-template-columns: 1fr; }
              #artifacts-pane { border-left: none; border-top: 1px solid rgba(255,255,255,.08); min-height: 360px; }
            }
          </style>
        </head>
        <body>
          <div class="shell">
            <aside>
              <h1>Tau Web UI</h1>
              <div id="status" class="meta">Loading status…</div>
              <div class="sidebar-actions">
                <button id="new-session">New Session</button>
                <button id="import-session" class="secondary">Import</button>
                <button id="refresh" class="secondary">Refresh</button>
              </div>
              <label class="import-toggle">
                <input id="import-current-branch-only" type="checkbox" />
                <span>Current branch only</span>
              </label>
              <input id="session-import-input" type="file" accept="application/json,application/x-ndjson,.json,.jsonl" hidden />
              <div id="sessions"></div>
            </aside>
            <main>
              <div class="toolbar">
                <h2>Conversation</h2>
                <div class="meta">Runtime chat workspace.</div>
              </div>
              <div class="settings stack">
                <div class="settings-grid">
                  <label>Title<input id="session-title" placeholder="Session title" /></label>
                  <label>Provider<select id="provider"></select></label>
                  <label>Model<select id="model"></select></label>
                  <button id="save-settings" class="secondary">Save</button>
                </div>
                <div id="session-meta" class="meta">No session selected.</div>
                <div id="auth-status" class="meta">Auth status unavailable.</div>
              </div>
              <div class="conversation-grid">
                <div id="messages"></div>
                <section id="artifacts-pane">
                  <div class="artifacts-head">
                    <h2>Artifacts</h2>
                    <button id="refresh-artifacts" class="secondary">Refresh</button>
                  </div>
                  <div id="artifacts-list"></div>
                  <div id="artifact-preview" class="artifact-empty">No artifacts.</div>
                </section>
              </div>
              <div class="composer">
                <div id="pending-attachments" class="attachments"></div>
                <div class="composer-row">
                  <textarea id="prompt" placeholder="Ask Tau to inspect code, explain architecture, or run the coding-agent runtime."></textarea>
                  <div class="composer-actions">
                    <button id="attach" class="secondary">Attach</button>
                    <button id="send">Send</button>
                  </div>
                </div>
                <input id="attachment-input" type="file" multiple hidden />
              </div>
            </main>
          </div>
          <script>
            let currentSessionId = null;
            let currentSession = null;
            let catalog = { providers: [] };
            let pendingAttachments = [];
            let currentArtifacts = [];
            let activeArtifactFile = null;
            const activeReplSandboxes = new Map();
            let replBridgeAbortController = null;
            let replBridgeLoopRunning = false;
            let replBridgeSessionId = null;
            const currentSessionStorageKey = 'tau.webui.currentSessionId';

            async function fetchJson(url, options) {
              const response = await fetch(url, options);
              if (!response.ok) throw new Error(await response.text());
              return await response.json();
            }

            function escapeHtml(text) {
              return (text || '')
                .replaceAll('&', '&amp;')
                .replaceAll('<', '&lt;')
                .replaceAll('>', '&gt;')
                .replaceAll('"', '&quot;');
            }

            function escapeAttribute(text) {
              return escapeHtml(text).replaceAll("'", '&#39;');
            }

            function artifactUrl(sessionId, fileName) {
              return `/api/sessions/${encodeURIComponent(sessionId)}/artifacts/${encodeURIComponent(fileName)}`;
            }

            function artifactSandboxId(artifact) {
              return `artifact-${currentSessionId || 'session'}-${artifact.fileName}`;
            }

            function isKnownArtifactSandbox(sandboxId) {
              return currentArtifacts.some(artifact => artifactSandboxId(artifact) === sandboxId);
            }

            function isHtmlArtifact(artifact) {
              return artifact &&
                ((artifact.mimeType || '').toLowerCase().includes('html') ||
                  (artifact.fileName || '').toLowerCase().endsWith('.html') ||
                  (artifact.fileName || '').toLowerCase().endsWith('.htm'));
            }

            function isImageArtifact(artifact) {
              return artifact && (artifact.mimeType || '').toLowerCase().startsWith('image/');
            }

            const textArtifactExtensions = new Set([
              'txt', 'json', 'xml', 'yaml', 'yml', 'toml', 'csv',
              'js', 'javascript', 'ts', 'typescript', 'jsx', 'tsx',
              'py', 'python', 'java', 'c', 'cpp', 'h', 'cs', 'fs',
              'php', 'rb', 'ruby', 'go', 'rust', 'swift', 'kotlin',
              'scala', 'dart', 'html', 'css', 'scss', 'sass', 'less',
              'sh', 'bash', 'ps1', 'bat', 'sql', 'r', 'matlab', 'julia',
              'lua', 'perl', 'vue', 'svelte'
            ]);

            function artifactExtension(artifact) {
              const name = (artifact?.fileName || '').toLowerCase();
              const dot = name.lastIndexOf('.');
              return dot >= 0 ? name.slice(dot + 1) : '';
            }

            function isMarkdownArtifact(artifact) {
              const mime = (artifact?.mimeType || '').toLowerCase();
              const ext = artifactExtension(artifact);
              return mime.includes('markdown') || ext === 'md' || ext === 'markdown';
            }

            function isSvgArtifact(artifact) {
              const mime = (artifact?.mimeType || '').toLowerCase();
              return mime.includes('svg') || artifactExtension(artifact) === 'svg';
            }

            function isPdfArtifact(artifact) {
              const mime = (artifact?.mimeType || '').toLowerCase();
              return mime.includes('pdf') || artifactExtension(artifact) === 'pdf';
            }

            function isTextArtifact(artifact) {
              const mime = (artifact?.mimeType || '').toLowerCase();
              return mime.startsWith('text/') ||
                mime.includes('json') ||
                mime.includes('xml') ||
                mime.includes('yaml') ||
                mime.includes('javascript') ||
                textArtifactExtensions.has(artifactExtension(artifact));
            }

            function artifactDataUrl(artifact, fallbackMime) {
              const content = artifact?.content || '';
              if (content.startsWith('data:')) return content;
              const mimeType = artifact?.mimeType || fallbackMime || 'application/octet-stream';
              if (mimeType.startsWith('text/') || mimeType.includes('svg') || mimeType.includes('json') || mimeType.includes('xml')) {
                return `data:${mimeType};charset=utf-8,${encodeURIComponent(content)}`;
              }
              return `data:${mimeType};base64,${content}`;
            }

            function artifactViewerHead(artifact, label, extra = '') {
              const meta = [label, artifact.mimeType || 'application/octet-stream', formatBytes(artifact.size || 0)]
                .filter(Boolean)
                .join(' | ');
              return `<div class="artifact-viewer-head"><div><div class="artifact-viewer-title">${escapeHtml(artifact.fileName)}</div><div>${escapeHtml(meta)}</div></div>${extra}</div>`;
            }

            function sandboxSafeJson(value) {
              return JSON.stringify(value || []).replace(/<\/script/gi, '<\\/script');
            }

            function collectSessionAttachments(session) {
              const attachments = [];
              for (const message of session?.messages || []) {
                if (message.role !== 'user' || !Array.isArray(message.attachments)) continue;
                for (const attachment of message.attachments) {
                  attachments.push({
                    id: attachment.id,
                    fileName: attachment.fileName,
                    mimeType: attachment.mimeType || 'application/octet-stream',
                    size: Number(attachment.size || 0),
                    content: attachment.content || '',
                    extractedText: attachment.extractedText || null
                  });
                }
              }
              return attachments;
            }

            function sandboxBridgeCode(sandboxId, attachments) {
              const attachmentsJson = sandboxSafeJson(attachments);
              return `<script>
                window.__completionCallbacks = [];
                window.__runtimePendingSends = [];
                window.sendRuntimeMessage = async (message) => {
                  const messageId = 'msg_' + Date.now() + '_' + Math.random().toString(36).slice(2);
                  return await new Promise((resolve, reject) => {
                    const timeout = setTimeout(() => {
                      window.removeEventListener('message', handler);
                      reject(new Error('Runtime message timeout'));
                    }, 30000);
                    const handler = event => {
                      const data = event.data || {};
                      if (data.type === 'runtime-response' && data.messageId === messageId) {
                        clearTimeout(timeout);
                        window.removeEventListener('message', handler);
                        if (data.success) resolve(data);
                        else reject(new Error(data.error || 'Operation failed'));
                      }
                    };
                    window.addEventListener('message', handler);
                    window.parent.postMessage({ ...message, sandboxId: ${JSON.stringify(sandboxId)}, messageId }, '*');
                  });
                };
                window.onCompleted = callback => window.__completionCallbacks.push(callback);
                window.attachments = ${attachmentsJson};
                window.listAttachments = () => (window.attachments || []).map(attachment => ({
                  id: attachment.id,
                  fileName: attachment.fileName,
                  mimeType: attachment.mimeType,
                  size: attachment.size
                }));
                window.readTextAttachment = attachmentId => {
                  const attachment = (window.attachments || []).find(item => item.id === attachmentId);
                  if (!attachment) throw new Error('Attachment not found: ' + attachmentId);
                  if (attachment.extractedText) return attachment.extractedText;
                  try {
                    return atob(attachment.content || '');
                  } catch {
                    throw new Error('Failed to decode text content for: ' + attachmentId);
                  }
                };
                window.readBinaryAttachment = attachmentId => {
                  const attachment = (window.attachments || []).find(item => item.id === attachmentId);
                  if (!attachment) throw new Error('Attachment not found: ' + attachmentId);
                  const bin = atob(attachment.content || '');
                  const bytes = new Uint8Array(bin.length);
                  for (let i = 0; i < bin.length; i += 1) bytes[i] = bin.charCodeAt(i);
                  return bytes;
                };
                const isJsonFile = filename => String(filename || '').endsWith('.json');
                window.listArtifacts = async () => {
                  const response = await window.sendRuntimeMessage({ type: 'artifact-operation', action: 'list' });
                  return response.result || [];
                };
                window.getArtifact = async filename => {
                  const response = await window.sendRuntimeMessage({ type: 'artifact-operation', action: 'get', filename });
                  const content = response.result;
                  if (isJsonFile(filename)) return JSON.parse(content);
                  return content;
                };
                const stringifyArtifactContent = content => typeof content === 'string' ? content : JSON.stringify(content, null, 2);
                window.createArtifact = async (filename, content, mimeType) => {
                  await window.sendRuntimeMessage({ type: 'artifact-operation', action: 'create', filename, content: stringifyArtifactContent(content), mimeType });
                };
                window.updateArtifact = async (filename, old_str, new_str) => {
                  await window.sendRuntimeMessage({ type: 'artifact-operation', action: 'update', filename, old_str, new_str });
                };
                window.rewriteArtifact = async (filename, content, mimeType) => {
                  await window.sendRuntimeMessage({ type: 'artifact-operation', action: 'rewrite', filename, content: stringifyArtifactContent(content), mimeType });
                };
                window.createOrUpdateArtifact = async (filename, content, mimeType) => {
                  const finalContent = stringifyArtifactContent(content);
                  await window.sendRuntimeMessage({ type: 'artifact-operation', action: 'createOrUpdate', filename, content: finalContent, mimeType });
                };
                window.htmlArtifactLogs = async filename => {
                  const response = await window.sendRuntimeMessage({ type: 'artifact-operation', action: 'htmlArtifactLogs', filename });
                  return response.result || '';
                };
                window.deleteArtifact = async filename => {
                  await window.sendRuntimeMessage({ type: 'artifact-operation', action: 'delete', filename });
                };
                const bytesToBase64 = bytes => {
                  let binary = '';
                  for (let i = 0; i < bytes.length; i += 1) binary += String.fromCharCode(bytes[i]);
                  return btoa(binary);
                };
                window.returnDownloadableFile = async (fileName, content, mimeType) => {
                  let finalContent;
                  let finalMimeType;
                  let finalContentBase64 = null;
                  if (content instanceof Blob) {
                    if (!mimeType && !content.type) {
                      throw new Error("returnDownloadableFile: MIME type is required for Blob content. Please provide a mimeType parameter (e.g., 'image/png').");
                    }
                    finalContent = bytesToBase64(new Uint8Array(await content.arrayBuffer()));
                    finalContentBase64 = finalContent;
                    finalMimeType = mimeType || content.type || 'application/octet-stream';
                  } else if (content instanceof Uint8Array) {
                    if (!mimeType) {
                      throw new Error("returnDownloadableFile: MIME type is required for Uint8Array content. Please provide a mimeType parameter (e.g., 'image/png').");
                    }
                    finalContent = bytesToBase64(content);
                    finalContentBase64 = finalContent;
                    finalMimeType = mimeType;
                  } else if (typeof content === 'string') {
                    finalContent = content;
                    finalMimeType = mimeType || 'text/plain';
                  } else {
                    finalContent = JSON.stringify(content, null, 2);
                    finalMimeType = mimeType || 'application/json';
                  }
                  const response = await window.sendRuntimeMessage({
                    type: 'file-returned',
                    fileName,
                    content: finalContent,
                    contentBase64: finalContentBase64,
                    isBase64: finalContentBase64 !== null,
                    mimeType: finalMimeType
                  });
                  if (response.error) throw new Error(response.error);
                  return { fileName, mimeType: finalMimeType };
                };
                ['log', 'warn', 'error', 'info'].forEach(method => {
                  const original = console[method].bind(console);
                  console[method] = (...args) => {
                    original(...args);
                    const text = args.map(arg => {
                      try { return typeof arg === 'object' ? JSON.stringify(arg) : String(arg); }
                      catch { return String(arg); }
                    }).join(' ');
                    const pending = window.sendRuntimeMessage({ type: 'console', method, text }).catch(() => {});
                    window.__runtimePendingSends.push(pending);
                  };
                });
                window.onCompleted(async () => {
                  const pending = window.__runtimePendingSends.splice(0);
                  if (pending.length > 0) await Promise.all(pending);
                });
                window.complete = async (error, returnValue) => {
                  for (const callback of window.__completionCallbacks) {
                    await callback(!error);
                  }
                  await window.sendRuntimeMessage(error
                    ? { type: 'execution-error', text: error.message || String(error), error }
                    : { type: 'execution-complete', returnValue });
                };
              <\/script>`;
            }

            function artifactSandboxSrcdoc(artifact) {
              const sandboxId = artifactSandboxId(artifact);
              return `${sandboxBridgeCode(sandboxId, collectSessionAttachments(currentSession))}${artifact.content || ''}`;
            }

            function escapeScriptContent(code) {
              return String(code || '').replace(/<\/script/gi, '<\\/script');
            }

            function replSandboxSrcdoc(sandboxId, code) {
              return `<!doctype html>
                <html>
                <head>${sandboxBridgeCode(sandboxId, collectSessionAttachments(currentSession))}</head>
                <body>
                <script type="module">
                (async () => {
                  try {
                    const userCodeFunc = async () => {
                      ${escapeScriptContent(code)}
                    };
                    const returnValue = await userCodeFunc();
                    await window.complete(null, returnValue);
                  } catch (error) {
                    await window.complete({
                      message: error?.message || String(error),
                      stack: error?.stack || new Error().stack
                    });
                  }
                })();
                <\/script>
                </body>
                </html>`;
            }

            function formatReplReturnValue(value) {
              if (value === undefined) return '';
              if (typeof value === 'object') {
                try {
                  return JSON.stringify(value, null, 2);
                } catch {
                  return String(value);
                }
              }
              return String(value);
            }

            function formatJavaScriptReplOutput(context, success, error) {
              const lines = [];
              for (const entry of context.console) {
                if (entry?.text) lines.push(entry.text);
              }

              if (!success) {
                if (lines.length > 0) lines.push('');
                const message = error?.message || String(error || 'Unknown error');
                const stack = error?.stack || '';
                lines.push(`Error: ${message}${stack ? `\n${stack}` : ''}`);
              } else if (context.returnValue !== undefined) {
                if (lines.length > 0) lines.push('');
                lines.push(`=> ${formatReplReturnValue(context.returnValue)}`);
              }

              if (context.files.length > 0) {
                lines.push(`[Files returned: ${context.files.length}]`);
                for (const file of context.files) {
                  lines.push(`  - ${file.fileName} (${file.mimeType})`);
                }
              } else if (context.code.includes('returnDownloadableFile')) {
                lines.push('[No files returned - check async operations]');
              }

              return lines.join('\n').trim() || 'Code executed successfully (no output)';
            }

            function bytesToBase64ForBridge(bytes) {
              let binary = '';
              const chunk = 0x8000;
              for (let i = 0; i < bytes.length; i += chunk) {
                binary += String.fromCharCode(...bytes.subarray(i, i + chunk));
              }
              return btoa(binary);
            }

            function encodeTextFileForBridge(content) {
              const bytes = new TextEncoder().encode(String(content ?? ''));
              return {
                contentBase64: bytesToBase64ForBridge(bytes),
                size: bytes.length
              };
            }

            function estimateBase64SizeForBridge(contentBase64) {
              const normalized = String(contentBase64 || '').replace(/\s/g, '');
              if (!normalized) return 0;
              const padding = normalized.endsWith('==') ? 2 : normalized.endsWith('=') ? 1 : 0;
              return Math.max(0, Math.floor(normalized.length * 3 / 4) - padding);
            }

            function normalizeReplFileForBridge(file) {
              const fileName = file?.fileName || 'file';
              const mimeType = file?.mimeType || 'application/octet-stream';
              if (file?.contentBase64 || file?.isBase64) {
                const contentBase64 = file.contentBase64 || file.content || '';
                return {
                  fileName,
                  mimeType,
                  size: estimateBase64SizeForBridge(contentBase64),
                  contentBase64
                };
              }

              return {
                fileName,
                mimeType,
                ...encodeTextFileForBridge(file?.content)
              };
            }

            function completeJavaScriptRepl(context, success, error) {
              if (context.completed) return;
              context.completed = true;
              clearTimeout(context.timeoutId);
              activeReplSandboxes.delete(context.sandboxId);
              context.iframe.remove();

              const result = {
                output: formatJavaScriptReplOutput(context, success, error),
                files: context.files,
                console: context.console,
                returnValue: context.returnValue
              };

              if (success) {
                context.resolve(result);
              } else {
                const failure = new Error(result.output);
                failure.result = result;
                context.reject(failure);
              }
            }

            async function executeJavaScriptRepl(code) {
              if (!currentSessionId) throw new Error('JavaScript REPL requires an active session.');
              if (!code) throw new Error('Code parameter is required.');

              const sessionId = currentSessionId;
              const sandboxId = `repl-${Date.now()}-${Math.random().toString(36).slice(2)}`;
              const iframe = document.createElement('iframe');
              iframe.sandbox.add('allow-scripts', 'allow-modals');
              iframe.style.cssText = 'width: 0; height: 0; border: 0; position: absolute; left: -10000px; top: -10000px;';

              const result = await new Promise((resolve, reject) => {
                const context = {
                  sandboxId,
                  sessionId,
                  code: String(code),
                  iframe,
                  console: [],
                  files: [],
                  returnValue: undefined,
                  completed: false,
                  resolve,
                  reject,
                  timeoutId: 0
                };
                context.timeoutId = setTimeout(() => {
                  completeJavaScriptRepl(context, false, { message: 'Execution timeout (120s)', stack: '' });
                }, 120000);
                activeReplSandboxes.set(sandboxId, context);
                iframe.srcdoc = replSandboxSrcdoc(sandboxId, context.code);
                document.body.appendChild(iframe);
              });

              await loadArtifacts(sessionId);
              return result;
            }

            window.executeJavaScriptRepl = executeJavaScriptRepl;

            function delay(ms, signal) {
              return new Promise((resolve, reject) => {
                const timeout = setTimeout(resolve, ms);
                if (!signal) return;
                const abort = () => {
                  clearTimeout(timeout);
                  reject(new DOMException('Aborted', 'AbortError'));
                };
                signal.addEventListener('abort', abort, { once: true });
              });
            }

            async function postJavaScriptReplBridgeResult(sessionId, requestId, payload, signal) {
              const response = await fetch(`/api/sessions/${encodeURIComponent(sessionId)}/javascript-repl/${encodeURIComponent(requestId)}/result`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(payload),
                signal
              });
              if (!response.ok) throw new Error(await response.text());
            }

            async function executeJavaScriptReplBridgeRequest(sessionId, request, signal) {
              try {
                const result = await executeJavaScriptRepl(request.code);
                await postJavaScriptReplBridgeResult(sessionId, request.id, {
                  output: result.output,
                  isError: false,
                  files: (result.files || []).map(normalizeReplFileForBridge)
                }, signal);
              } catch (error) {
                const result = error?.result || {};
                await postJavaScriptReplBridgeResult(sessionId, request.id, {
                  output: result.output || error.message || String(error),
                  isError: true,
                  error: error.message || String(error),
                  files: (result.files || []).map(normalizeReplFileForBridge)
                }, signal);
              }
            }

            async function runJavaScriptReplBridgeLoop(sessionId, controller) {
              try {
                while (!controller.signal.aborted && currentSessionId === sessionId) {
                  try {
                    const response = await fetch(`/api/sessions/${encodeURIComponent(sessionId)}/javascript-repl/next?timeoutMs=25000`, {
                      signal: controller.signal
                    });
                    if (response.status === 204) continue;
                    if (response.status === 404) {
                      await delay(1000, controller.signal);
                      continue;
                    }
                    if (!response.ok) throw new Error(await response.text());
                    const request = await response.json();
                    if (request?.id) {
                      await executeJavaScriptReplBridgeRequest(sessionId, request, controller.signal);
                    }
                  } catch (error) {
                    if (controller.signal.aborted) break;
                    await delay(1000, controller.signal).catch(() => {});
                  }
                }
              } finally {
                if (replBridgeAbortController === controller) {
                  replBridgeAbortController = null;
                  replBridgeLoopRunning = false;
                  replBridgeSessionId = null;
                }
              }
            }

            function startJavaScriptReplBridgeLoop(sessionId) {
              if (!sessionId) {
                if (replBridgeAbortController) replBridgeAbortController.abort();
                replBridgeAbortController = null;
                replBridgeLoopRunning = false;
                replBridgeSessionId = null;
                return;
              }

              if (replBridgeLoopRunning && replBridgeSessionId === sessionId) return;
              if (replBridgeAbortController) replBridgeAbortController.abort();
              const controller = new AbortController();
              replBridgeAbortController = controller;
              replBridgeLoopRunning = true;
              replBridgeSessionId = sessionId;
              runJavaScriptReplBridgeLoop(sessionId, controller);
            }

            function formatBytes(size) {
              const value = Number(size || 0);
              if (value < 1024) return `${value} B`;
              if (value < 1024 * 1024) return `${(value / 1024).toFixed(1)} KB`;
              return `${(value / 1024 / 1024).toFixed(1)} MB`;
            }

            function rememberCurrentSession(id) {
              try {
                if (id) {
                  localStorage.setItem(currentSessionStorageKey, id);
                } else {
                  localStorage.removeItem(currentSessionStorageKey);
                }
              } catch {
                // Ignore storage access failures in restricted browser contexts.
              }
            }

            function readRememberedSession() {
              try {
                return localStorage.getItem(currentSessionStorageKey);
              } catch {
                return null;
              }
            }

            function attachmentPreviewSrc(attachment) {
              const data = attachment.preview || (attachment.type === 'image' ? attachment.content : null);
              if (!data) return null;
              const mimeType = attachment.type === 'image' ? attachment.mimeType : 'image/png';
              return `data:${mimeType};base64,${data}`;
            }

            function renderAttachments(attachments, editable) {
              if (!attachments || !attachments.length) return '';
              return `<div class="attachments">${attachments.map(attachment => {
                const preview = attachmentPreviewSrc(attachment);
                const remove = editable ? `<button class="attachment-remove" data-attachment-id="${escapeAttribute(attachment.id)}">Remove</button>` : '';
                return `<div class="attachment">
                  ${preview ? `<img src="${escapeAttribute(preview)}" alt="" />` : '<div class="attachment-icon">FILE</div>'}
                  <div>
                    <div class="attachment-name">${escapeHtml(attachment.fileName || 'attachment')}</div>
                    <div class="attachment-meta">${escapeHtml(attachment.mimeType || 'application/octet-stream')} | ${formatBytes(attachment.size)}</div>
                  </div>
                  ${remove}
                </div>`;
              }).join('')}</div>`;
            }

            function renderPendingAttachments() {
              const root = document.getElementById('pending-attachments');
              root.innerHTML = renderAttachments(pendingAttachments, true);
              root.querySelectorAll('.attachment-remove').forEach(node => {
                node.addEventListener('click', () => {
                  pendingAttachments = pendingAttachments.filter(attachment => attachment.id !== node.dataset.attachmentId);
                  renderPendingAttachments();
                });
              });
            }

            async function loadArtifacts(sessionId) {
              if (!sessionId) {
                currentArtifacts = [];
                activeArtifactFile = null;
                renderArtifacts();
                return;
              }

              currentArtifacts = await fetchJson(`/api/sessions/${encodeURIComponent(sessionId)}/artifacts`);
              if (activeArtifactFile && !currentArtifacts.some(artifact => artifact.fileName === activeArtifactFile)) {
                activeArtifactFile = null;
              }
              if (!activeArtifactFile && currentArtifacts.length) {
                activeArtifactFile = currentArtifacts[0].fileName;
              }
              renderArtifacts();
            }

            async function loadArtifactContent(fileName) {
              if (!currentSessionId || !fileName) return null;
              return await fetchJson(artifactUrl(currentSessionId, fileName));
            }

            async function selectArtifact(fileName) {
              activeArtifactFile = fileName;
              renderArtifacts();
              const artifact = await loadArtifactContent(fileName);
              if (artifact) {
                renderArtifactPreview(artifact);
              }
            }

            function renderArtifacts() {
              const list = document.getElementById('artifacts-list');
              if (!list) return;
              list.innerHTML = currentArtifacts.map(artifact => `
                <button class="artifact-pill ${artifact.fileName === activeArtifactFile ? 'active' : ''}" data-file="${escapeAttribute(artifact.fileName)}">
                  ${escapeHtml(artifact.fileName)}
                </button>`).join('');
              list.querySelectorAll('.artifact-pill').forEach(node => {
                node.addEventListener('click', () => selectArtifact(node.dataset.file));
              });

              const preview = document.getElementById('artifact-preview');
              if (!currentArtifacts.length) {
                preview.className = 'artifact-empty';
                preview.textContent = 'No artifacts.';
                return;
              }

              if (activeArtifactFile) {
                loadArtifactContent(activeArtifactFile)
                  .then(artifact => artifact && renderArtifactPreview(artifact))
                  .catch(error => {
                    preview.className = 'artifact-empty';
                    preview.textContent = `artifact failed: ${error.message || error}`;
                  });
              }
            }

            function renderArtifactPreview(artifact) {
              const preview = document.getElementById('artifact-preview');
              preview.className = '';
              if (isHtmlArtifact(artifact)) {
                preview.innerHTML = `<div class="artifact-viewer">${artifactViewerHead(artifact, 'HTML artifact')}<iframe class="artifact-frame" sandbox="allow-scripts allow-forms" srcdoc="${escapeAttribute(artifactSandboxSrcdoc(artifact))}"></iframe></div>`;
                return;
              }

              if (isMarkdownArtifact(artifact)) {
                preview.innerHTML = `<div class="artifact-viewer">${artifactViewerHead(artifact, 'Markdown preview')}<div class="message-text artifact-markdown">${renderText(artifact.content || '')}</div></div>`;
                return;
              }

              if (isSvgArtifact(artifact)) {
                const download = `<a class="artifact-download-link" download="${escapeAttribute(artifact.fileName)}" href="${escapeAttribute(artifactDataUrl(artifact, 'image/svg+xml'))}">Download</a>`;
                preview.innerHTML = `<div class="artifact-viewer">${artifactViewerHead(artifact, 'SVG preview', download)}<iframe class="artifact-frame" sandbox="" srcdoc="${escapeAttribute(artifact.content || '')}"></iframe></div>`;
                return;
              }

              if (isImageArtifact(artifact)) {
                const src = artifact.content.startsWith('data:')
                  ? artifact.content
                  : `data:${artifact.mimeType};base64,${artifact.content}`;
                const download = `<a class="artifact-download-link" download="${escapeAttribute(artifact.fileName)}" href="${escapeAttribute(src)}">Download</a>`;
                preview.innerHTML = `<div class="artifact-viewer">${artifactViewerHead(artifact, 'Image preview', download)}<img class="artifact-image" src="${escapeAttribute(src)}" alt="" /></div>`;
                return;
              }

              if (isPdfArtifact(artifact)) {
                const src = artifactDataUrl(artifact, 'application/pdf');
                const download = `<a class="artifact-download-link" download="${escapeAttribute(artifact.fileName)}" href="${escapeAttribute(src)}">Download</a>`;
                preview.innerHTML = `<div class="artifact-viewer">${artifactViewerHead(artifact, 'PDF preview', download)}<iframe class="artifact-frame" src="${escapeAttribute(src)}"></iframe></div>`;
                return;
              }

              if (isTextArtifact(artifact)) {
                preview.innerHTML = `<div class="artifact-viewer">${artifactViewerHead(artifact, 'Text preview')}<pre class="artifact-code"><code>${escapeHtml(artifact.content || '')}</code></pre></div>`;
                return;
              }

              const download = `<a class="artifact-download-link" download="${escapeAttribute(artifact.fileName)}" href="${escapeAttribute(artifactDataUrl(artifact, artifact.mimeType || 'application/octet-stream'))}">Download</a>`;
              preview.innerHTML = `<div class="artifact-generic"><div class="artifact-generic-card">${artifactViewerHead(artifact, 'Generic file', download)}<div>Preview not available for this file type.</div></div></div>`;
            }

            function isTextFile(file) {
              const name = (file.name || '').toLowerCase();
              return (file.type || '').startsWith('text/') ||
                ['.txt', '.md', '.json', '.xml', '.html', '.css', '.js', '.ts', '.jsx', '.tsx', '.yml', '.yaml', '.cs', '.fs', '.ps1', '.sh', '.sql', '.csv'].some(ext => name.endsWith(ext));
            }

            function readAsDataUrl(file) {
              return new Promise((resolve, reject) => {
                const reader = new FileReader();
                reader.onload = () => resolve(String(reader.result || ''));
                reader.onerror = () => reject(reader.error || new Error('Failed to read attachment'));
                reader.readAsDataURL(file);
              });
            }

            async function readAttachment(file) {
              const dataUrl = await readAsDataUrl(file);
              const base64 = dataUrl.includes(',') ? dataUrl.split(',').pop() : dataUrl;
              let extractedText = null;
              if (isTextFile(file)) {
                const text = await file.text();
                extractedText = text.length > 1024 * 1024
                  ? `${text.slice(0, 1024 * 1024)}\n[Attachment text truncated at 1 MB.]`
                  : text;
              }

              const mimeType = file.type || (isTextFile(file) ? 'text/plain' : 'application/octet-stream');
              return {
                id: `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`,
                type: mimeType.startsWith('image/') ? 'image' : 'document',
                fileName: file.name || 'attachment',
                mimeType,
                size: file.size || 0,
                content: base64,
                extractedText,
                preview: mimeType.startsWith('image/') ? base64 : null
              };
            }

            function isWordChar(ch) {
              return !!ch && /[A-Za-z0-9]/.test(ch);
            }

            function findMarkdownLink(source, index) {
              const pattern = /\[([^\]\n]+)\]\((https?:\/\/[^\s)]+)\)/g;
              pattern.lastIndex = index;
              const match = pattern.exec(source);
              if (!match) return null;
              return { index: match.index, length: match[0].length, label: match[1], url: match[2] };
            }

            function findBareUrl(source, index) {
              const pattern = /https?:\/\/[^\s<>()]+/g;
              pattern.lastIndex = index;
              const match = pattern.exec(source);
              if (!match) return null;
              const rawUrl = match[0];
              const trimmedUrl = rawUrl.replace(/[.,;:!?]+$/, '');
              return {
                index: match.index,
                length: trimmedUrl.length,
                trailing: rawUrl.slice(trimmedUrl.length),
                url: trimmedUrl
              };
            }

            function findDelimitedSpan(source, index, marker, tag) {
              let start = source.indexOf(marker, index);
              while (start >= 0) {
                const before = source[start - 1] || '';
                const after = source[start + marker.length] || '';
                if (marker === '_' && (isWordChar(before) || isWordChar(after))) {
                  start = source.indexOf(marker, start + marker.length);
                  continue;
                }

                const end = source.indexOf(marker, start + marker.length);
                if (end > start + marker.length) {
                  const beforeEnd = source[end - 1] || '';
                  const afterEnd = source[end + marker.length] || '';
                  if (marker === '_' && (isWordChar(beforeEnd) || isWordChar(afterEnd))) {
                    start = source.indexOf(marker, start + marker.length);
                    continue;
                  }

                  return {
                    index: start,
                    length: end + marker.length - start,
                    inner: source.slice(start + marker.length, end),
                    tag
                  };
                }

                start = source.indexOf(marker, start + marker.length);
              }

              return null;
            }

            function renderDecoratedText(source) {
              let output = '';
              let index = 0;
              while (index < source.length) {
                const candidates = [
                  findMarkdownLink(source, index),
                  findBareUrl(source, index),
                  findDelimitedSpan(source, index, '**', 'strong'),
                  findDelimitedSpan(source, index, '__', 'strong'),
                  findDelimitedSpan(source, index, '*', 'em'),
                  findDelimitedSpan(source, index, '_', 'em')
                ].filter(Boolean).sort((a, b) => a.index - b.index || a.length - b.length);
                const next = candidates[0];
                if (!next) {
                  output += escapeHtml(source.slice(index));
                  break;
                }

                output += escapeHtml(source.slice(index, next.index));
                if (next.label !== undefined) {
                  output += `<a href="${escapeAttribute(next.url)}" target="_blank" rel="noreferrer noopener">${renderDecoratedText(next.label)}</a>`;
                } else if (next.url !== undefined) {
                  output += `<a href="${escapeAttribute(next.url)}" target="_blank" rel="noreferrer noopener">${escapeHtml(next.url)}</a>${escapeHtml(next.trailing || '')}`;
                } else {
                  output += `<${next.tag}>${renderDecoratedText(next.inner)}</${next.tag}>`;
                }

                index = next.index + next.length + (next.trailing ? next.trailing.length : 0);
              }
              return output;
            }

            function renderInline(text) {
              const source = text || '';
              let output = '';
              let index = 0;
              while (index < source.length) {
                const tick = source.indexOf('`', index);
                if (tick < 0) {
                  output += renderDecoratedText(source.slice(index));
                  break;
                }

                output += renderDecoratedText(source.slice(index, tick));
                const end = source.indexOf('`', tick + 1);
                if (end < 0) {
                  output += '&#96;';
                  index = tick + 1;
                  continue;
                }

                output += `<code class="inline-code">${escapeHtml(source.slice(tick + 1, end))}</code>`;
                index = end + 1;
              }
              return output;
            }

            function isTableSeparator(line) {
              return /^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)+\|?\s*$/.test(line || '');
            }

            function splitTableRow(line) {
              let value = line.trim();
              if (value.startsWith('|')) value = value.slice(1);
              if (value.endsWith('|')) value = value.slice(0, -1);
              return value.split('|').map(cell => cell.trim());
            }

            function isTableStart(lines, index) {
              return index + 1 < lines.length &&
                (lines[index] || '').includes('|') &&
                isTableSeparator(lines[index + 1]);
            }

            function isBlockStart(lines, index) {
              const line = lines[index] || '';
              return isTableStart(lines, index) ||
                /^(#{1,6})\s+\S/.test(line) ||
                /^\s*>\s?/.test(line) ||
                /^\s*(?:[-*+]\s+|\d+\.\s+)/.test(line);
            }

            function renderTable(rows) {
              const header = splitTableRow(rows[0]);
              const body = rows.slice(2).map(splitTableRow);
              return `<div class="table-wrap"><table><thead><tr>${header.map(cell => `<th>${renderInline(cell)}</th>`).join('')}</tr></thead><tbody>${body.map(row => `<tr>${row.map(cell => `<td>${renderInline(cell)}</td>`).join('')}</tr>`).join('')}</tbody></table></div>`;
            }

            function renderListItem(text) {
              const task = text.match(/^\s*\[([ xX])\]\s+(.*)$/);
              if (!task) return renderInline(text);
              const checked = task[1].toLowerCase() === 'x' ? ' checked' : '';
              return `<input class="task-check" type="checkbox" disabled${checked}>${renderInline(task[2])}`;
            }

            function renderBlocks(lines) {
              const chunks = [];
              let index = 0;
              while (index < lines.length) {
                if (!lines[index].trim()) {
                  index++;
                  continue;
                }

                if (isTableStart(lines, index)) {
                  const table = [lines[index], lines[index + 1]];
                  index += 2;
                  while (index < lines.length && lines[index].trim() && lines[index].includes('|')) {
                    table.push(lines[index++]);
                  }
                  chunks.push(renderTable(table));
                  continue;
                }

                const heading = lines[index].match(/^(#{1,6})\s+(.+)$/);
                if (heading) {
                  const level = heading[1].length;
                  chunks.push(`<h${level}>${renderInline(heading[2])}</h${level}>`);
                  index++;
                  continue;
                }

                if (/^\s*>\s?/.test(lines[index])) {
                  const quote = [];
                  while (index < lines.length && /^\s*>\s?/.test(lines[index])) {
                    quote.push(lines[index].replace(/^\s*>\s?/, ''));
                    index++;
                  }
                  chunks.push(`<blockquote>${renderBlocks(quote)}</blockquote>`);
                  continue;
                }

                const listMatch = lines[index].match(/^\s*((?:[-*+])|\d+\.)\s+(.+)$/);
                if (listMatch) {
                  const ordered = /\d+\./.test(listMatch[1]);
                  const tag = ordered ? 'ol' : 'ul';
                  const items = [];
                  while (index < lines.length) {
                    const item = lines[index].match(/^\s*((?:[-*+])|\d+\.)\s+(.+)$/);
                    if (!item || /\d+\./.test(item[1]) !== ordered) break;
                    items.push(`<li>${renderListItem(item[2])}</li>`);
                    index++;
                  }
                  chunks.push(`<${tag}>${items.join('')}</${tag}>`);
                  continue;
                }

                const paragraph = [];
                while (index < lines.length && lines[index].trim() && !isBlockStart(lines, index)) {
                  paragraph.push(lines[index++]);
                }
                if (paragraph.length) {
                  chunks.push(`<div>${renderInline(paragraph.join('\n'))}</div>`);
                }
              }

              return chunks.join('');
            }

            function renderText(text) {
              const lines = (text || '').replaceAll('\r\n', '\n').split('\n');
              const chunks = [];
              let prose = [];
              let code = [];
              let codeLanguage = '';
              let inCode = false;

              function flushProse() {
                if (!prose.length) return;
                chunks.push(renderBlocks(prose));
                prose = [];
              }

              function flushCode() {
                const label = codeLanguage ? `<span class="code-label">${escapeHtml(codeLanguage)}</span>` : '';
                chunks.push(`<pre>${label}<code${codeLanguage ? ` data-language="${escapeAttribute(codeLanguage)}"` : ''}>${escapeHtml(code.join('\n'))}</code></pre>`);
                code = [];
                codeLanguage = '';
              }

              for (const line of lines) {
                if (line.startsWith('```')) {
                  if (inCode) {
                    flushCode();
                    inCode = false;
                  } else {
                    flushProse();
                    codeLanguage = line.slice(3).trim();
                    inCode = true;
                  }
                  continue;
                }

                if (inCode) {
                  code.push(line);
                } else {
                  prose.push(line);
                }
              }

              if (inCode) flushCode();
              flushProse();
              return chunks.join('');
            }

            function formatJsonLike(value) {
              if (!value) return '';
              try {
                return JSON.stringify(JSON.parse(value), null, 2);
              } catch {
                return value;
              }
            }

            function renderThinking(thinking, streaming) {
              if (!thinking) return '';
              const open = streaming ? ' open' : '';
              const label = streaming ? 'Thinking...' : 'Thinking';
              return `<div class="thinking"><details${open}><summary>${label}</summary><div class="thinking-content">${renderText(thinking)}</div></details></div>`;
            }

            function renderToolCalls(toolCalls, legacyEvents) {
              if (toolCalls && toolCalls.length) {
                return `<div class="tooling"><div class="tooling-title">Tool timeline</div>${toolCalls.map(tool => {
                  const status = tool.status || 'unknown';
                  const args = formatJsonLike(tool.arguments || '');
                  const output = formatJsonLike(tool.output || '');
                  const updates = tool.updates && tool.updates.length ? tool.updates.join('\n') : '';
                  const started = tool.startedAt ? `started ${new Date(tool.startedAt).toLocaleTimeString()}` : '';
                  const completed = tool.completedAt ? `completed ${new Date(tool.completedAt).toLocaleTimeString()}` : '';
                  return `<div class="tool-card">
                    <div class="tool-head">
                      <span class="tool-status ${escapeAttribute(status)}">${escapeHtml(status)}</span>
                      <span class="tool-name">${escapeHtml(tool.toolName || 'tool')}</span>
                      <span class="tool-id">${escapeHtml(tool.id || '')}</span>
                    </div>
                    ${started || completed ? `<div class="meta">${escapeHtml([started, completed].filter(Boolean).join(' | '))}</div>` : ''}
                    ${args ? `<details${status === 'preparing' || status === 'running' ? ' open' : ''}><summary>Input</summary><pre><code data-language="json">${escapeHtml(args)}</code></pre></details>` : ''}
                    ${updates ? `<details><summary>Updates</summary><pre><code>${escapeHtml(updates)}</code></pre></details>` : ''}
                    ${output ? `<details${tool.isError ? ' open' : ''}><summary>Output</summary><pre><code>${escapeHtml(output)}</code></pre></details>` : ''}
                  </div>`;
                }).join('')}</div>`;
              }

              if (legacyEvents && legacyEvents.length) {
                return `<div class="tooling"><div class="tooling-title">Tools</div><pre><code>${escapeHtml(legacyEvents.join('\n'))}</code></pre></div>`;
              }

              return '';
            }

            function mergeToolCall(message, incoming) {
              if (!incoming || !incoming.id) return;
              const list = message.toolCalls || (message.toolCalls = []);
              const index = list.findIndex(tool => tool.id === incoming.id);
              if (index < 0) {
                list.push(incoming);
                return;
              }

              list[index] = { ...list[index], ...incoming };
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

            function createDefaultSessionMeta(session) {
              if (!session) return 'No session selected.';
              const persisted = session.persisted ? 'persisted' : 'memory';
              const messageCount = Array.isArray(session.messages) ? session.messages.length : 0;
              return `provider=${session.provider} | model=${session.model} | messages=${messageCount} | storage=${persisted}`;
            }

            function collectCodingAgentWarningCodes(importStrategy, audit) {
              const fromStrategy = Array.isArray(importStrategy?.warningCodes)
                ? importStrategy.warningCodes.filter(Boolean)
                : [];
              const fromAudit = Array.isArray(audit?.warnings)
                ? audit.warnings.map(warning => warning?.code).filter(Boolean)
                : [];
              return [...new Set([...fromStrategy, ...fromAudit])];
            }

            function createCodingAgentImportSummaryText(importedMessageCount, sourceEntryCount, sourceMessageCount, warningCount, currentBranchOnly, importStrategy, audit, verb) {
              const scopedCurrentBranchOnly = typeof importStrategy?.currentBranchOnly === 'boolean'
                ? importStrategy.currentBranchOnly
                : currentBranchOnly;
              const parts = [
                `${pluralize(safeCount(importedMessageCount, 0), 'message')} imported`,
                `source ${pluralize(safeCount(sourceEntryCount, 0), 'entry', 'entries')} / ${pluralize(safeCount(sourceMessageCount, safeCount(importedMessageCount, 0)), 'message')}`,
                `${pluralize(safeCount(warningCount, 0), 'warning')}`
              ];
              parts.push(`currentBranchOnly=${scopedCurrentBranchOnly === true ? 'true' : scopedCurrentBranchOnly === false ? 'false' : 'unknown'}`);
              if (importStrategy?.strategy) {
                parts.push(`strategy=${importStrategy.strategy}`);
              }
              if (typeof importStrategy?.importsTimelineMessagesOnly === 'boolean') {
                parts.push(`timelineOnly=${importStrategy.importsTimelineMessagesOnly}`);
              }
              if (typeof importStrategy?.persistsBranchTree === 'boolean') {
                parts.push(`branchTreePersisted=${importStrategy.persistsBranchTree}`);
                if (!importStrategy.persistsBranchTree) {
                  parts.push('branch tree not persisted');
                }
              }
              if (importStrategy?.sourceLeafEntryId) {
                parts.push(`leaf=${importStrategy.sourceLeafEntryId}`);
              }
              const warningCodes = collectCodingAgentWarningCodes(importStrategy, audit);
              if (warningCodes.length > 0) {
                parts.push(`warningCodes=${warningCodes.join(',')}`);
              } else if (safeCount(warningCount, 0) === 0) {
                parts.push('warningCodes=none');
              }

              return `${verb || 'imported'} CodingAgent JSONL: ${parts.join(' | ')}`;
            }

            function createCodingAgentPreviewNotice(preview) {
              if (!preview?.importStrategy) return null;
              return createCodingAgentImportSummaryText(
                preview.audit?.importedMessageCount,
                preview.entryCount,
                preview.messageCount,
                preview.audit?.warnings?.length,
                preview.audit?.willImportCurrentBranchOnly,
                preview.importStrategy,
                preview.audit,
                'preview');
            }

            function createCodingAgentSourceMetadataNotice(sourceMetadata, importedMessageCount, summary, sourceStrategy, sourceAudit) {
              const importStrategy = sourceMetadata?.importStrategy || sourceStrategy || summary?.sourceStrategy;
              const audit = sourceMetadata?.audit || sourceAudit || summary?.sourceAudit;
              if (summary) {
                return createCodingAgentImportSummaryText(
                  summary.importedMessageCount,
                  summary.sourceEntryCount,
                  summary.sourceMessageCount,
                  summary.warningCount,
                  summary.currentBranchOnly,
                  importStrategy,
                  audit);
              }

              if (sourceMetadata?.kind !== 'coding-agent-jsonl') {
                return null;
              }

              return createCodingAgentImportSummaryText(
                importedMessageCount,
                sourceMetadata.entryCount,
                sourceMetadata.messageCount,
                sourceMetadata.audit?.warnings?.length,
                sourceMetadata.audit?.willImportCurrentBranchOnly,
                importStrategy,
                audit);
            }

            function createSessionMetaText(session) {
              if (!session) return 'No session selected.';
              return createCodingAgentSourceMetadataNotice(
                session.sourceMetadata,
                Array.isArray(session.messages) ? session.messages.length : 0)
                || createDefaultSessionMeta(session);
            }

            function applySessionSettings(session) {
              document.getElementById('session-title').value = session?.title || '';
              const providerSelect = document.getElementById('provider');
              providerSelect.value = session?.provider || providerSelect.value;
              bindModels(providerSelect.value, session?.model);
              document.getElementById('session-meta').textContent = createSessionMetaText(session);
              refreshAuthStatus(providerSelect.value, document.getElementById('model').value);
            }

            async function refreshAuthStatus(providerId, modelId) {
              const root = document.getElementById('auth-status');
              if (!providerId) {
                root.textContent = 'Auth status unavailable.';
                return;
              }

              try {
                const query = modelId ? `?model=${encodeURIComponent(modelId)}` : '';
                const status = await fetchJson(`/api/auth/${encodeURIComponent(providerId)}${query}`);
                const configured = status.isConfigured ? 'configured' : 'missing';
                const login = status.canLogin ? 'available' : 'not available';
                const oauth = status.usesOAuth ? 'yes' : 'no';
                root.textContent = `auth=${configured} | source=${status.source} | oauth=${oauth} | login=${login}\n${status.message}`;
              } catch (error) {
                root.textContent = `auth status failed: ${error.message || error}`;
              }
            }

            function renderMessages(session) {
              const root = document.getElementById('messages');
              if (!session) {
                root.innerHTML = '<div class="meta">Create a session to start chatting.</div>';
                return;
              }
              root.innerHTML = (session.messages || []).map(message => `
                <div class="message ${message.role}">
                  <div class="role">${message.role}</div>
                  <div class="message-text">${renderText(message.text || '')}</div>
                  ${renderAttachments(message.attachments || [], false)}
                  ${renderThinking(message.thinking, !!message.streaming)}
                  ${renderToolCalls(message.toolCalls || [], message.toolEvents || [])}
                  ${message.error ? `<div class="error">error\n${escapeHtml(message.error)}</div>` : ''}
                </div>`).join('');
              root.scrollTop = root.scrollHeight;
            }

            function createStreamMessage(role, event) {
              return {
                role,
                text: event.text || '',
                timestamp: event.timestamp || new Date().toISOString(),
                thinking: event.thinking || null,
                toolEvents: event.toolEvent ? [event.toolEvent] : null,
                error: event.error || null,
                attachments: event.attachments || null,
                toolCalls: event.toolCall ? [event.toolCall] : null
              };
            }

            function ensureStreamingAssistant(session, event) {
              const messages = session.messages || (session.messages = []);
              const last = messages[messages.length - 1];
              if (last && last.role === 'assistant' && last.streaming) {
                return last;
              }

              const assistant = createStreamMessage('assistant', event || {});
              assistant.streaming = true;
              messages.push(assistant);
              return assistant;
            }

            function applyStreamEvent(session, event) {
              if (!event || !event.type) return session;
              if (event.type === 'done') {
                return event.session || session;
              }

              if (event.type === 'user') {
                (session.messages || (session.messages = [])).push(createStreamMessage('user', event));
                return session;
              }

              const assistant = ensureStreamingAssistant(session, event);
              assistant.timestamp = event.timestamp || assistant.timestamp;
              if (event.toolCall) {
                mergeToolCall(assistant, event.toolCall);
              }
              if (event.type === 'text_delta') {
                assistant.text = (assistant.text || '') + (event.text || '');
              } else if (event.type === 'thinking_delta') {
                assistant.thinking = (assistant.thinking || '') + (event.thinking || '');
              } else if (event.type === 'tool_start' || event.type === 'tool_update' || event.type === 'tool_end' || event.type === 'tool_call') {
                if (event.toolEvent) {
                  assistant.toolEvents = assistant.toolEvents || [];
                  assistant.toolEvents.push(event.toolEvent);
                }
              } else if (event.type === 'error') {
                assistant.error = event.error || 'Unknown error';
              }
              return session;
            }

            async function sendMessageStreaming(sessionId, text, attachments) {
              let session = await fetchJson(`/api/sessions/${sessionId}`);
              currentSession = session;
              const response = await fetch(`/api/sessions/${sessionId}/messages/stream`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ text, attachments })
              });
              if (!response.ok) throw new Error(await response.text());
              if (!response.body) {
                return session;
              }

              const reader = response.body.getReader();
              const decoder = new TextDecoder();
              let buffer = '';
              while (true) {
                const { value, done } = await reader.read();
                if (done) break;
                buffer += decoder.decode(value, { stream: true });
                const lines = buffer.split('\n');
                buffer = lines.pop() || '';
                for (const line of lines) {
                  if (!line.trim()) continue;
                  const event = JSON.parse(line);
                  session = applyStreamEvent(session, event);
                  currentSession = session;
                  renderMessages(session);
                  applySessionSettings(session);
                  if (event.type === 'tool_end' && ['artifacts', 'javascript_repl'].includes(event.toolCall?.toolName)) {
                    await loadArtifacts(sessionId);
                  }
                }
              }

              buffer += decoder.decode();
              if (buffer.trim()) {
                const event = JSON.parse(buffer);
                session = applyStreamEvent(session, event);
                currentSession = session;
                renderMessages(session);
                applySessionSettings(session);
                if (event.type === 'tool_end' && ['artifacts', 'javascript_repl'].includes(event.toolCall?.toolName)) {
                  await loadArtifacts(sessionId);
                }
              }
              return session;
            }

            async function handleReplRuntimeMessage(context, message, source) {
              const respond = response => {
                source?.postMessage({
                  type: 'runtime-response',
                  messageId: message.messageId,
                  sandboxId: message.sandboxId,
                  ...response
                }, '*');
              };

              try {
                if (message.type === 'console') {
                  context.console.push({ type: message.method || 'log', text: message.text || '' });
                  respond({ success: true });
                  return;
                }

                if (message.type === 'file-returned') {
                  context.files.push({
                    fileName: message.fileName || 'file',
                    content: message.content,
                    contentBase64: message.contentBase64 || null,
                    isBase64: Boolean(message.isBase64),
                    mimeType: message.mimeType || 'application/octet-stream'
                  });
                  respond({ success: true, result: { fileName: message.fileName, mimeType: message.mimeType } });
                  return;
                }

                if (message.type === 'execution-complete') {
                  context.returnValue = message.returnValue;
                  respond({ success: true });
                  completeJavaScriptRepl(context, true);
                  return;
                }

                if (message.type === 'execution-error') {
                  respond({ success: false, error: message.text || message.error?.message || 'Sandbox execution failed.' });
                  completeJavaScriptRepl(context, false, message.error || { message: message.text || 'Sandbox execution failed.', stack: '' });
                  return;
                }

                const sessionId = context.sessionId || currentSessionId;
                const response = await fetchJson(`/api/sessions/${encodeURIComponent(sessionId)}/runtime/messages`, {
                  method: 'POST',
                  headers: { 'Content-Type': 'application/json' },
                  body: JSON.stringify(message)
                });
                if (message.type === 'artifact-operation' && ['createOrUpdate', 'create', 'update', 'rewrite', 'delete'].includes(message.action)) {
                  await loadArtifacts(sessionId);
                }
                source?.postMessage(response, '*');
              } catch (error) {
                respond({ success: false, error: error.message || String(error) });
              }
            }

            window.addEventListener('message', async event => {
              const message = event.data || {};
              if (!message.sandboxId || !message.messageId || !currentSessionId) return;
              const replContext = activeReplSandboxes.get(message.sandboxId);
              if (replContext) {
                await handleReplRuntimeMessage(replContext, message, event.source);
                return;
              }
              if (!isKnownArtifactSandbox(message.sandboxId)) return;
              try {
                const response = await fetchJson(`/api/sessions/${encodeURIComponent(currentSessionId)}/runtime/messages`, {
                  method: 'POST',
                  headers: { 'Content-Type': 'application/json' },
                  body: JSON.stringify(message)
                });
                if (message.type === 'artifact-operation' && ['createOrUpdate', 'create', 'update', 'rewrite', 'delete'].includes(message.action)) {
                  await loadArtifacts(currentSessionId);
                }
                event.source?.postMessage(response, '*');
              } catch (error) {
                event.source?.postMessage({
                  type: 'runtime-response',
                  messageId: message.messageId,
                  sandboxId: message.sandboxId,
                  success: false,
                  error: error.message || String(error)
                }, '*');
              }
            });

            function exportSession(sessionId) {
              window.location.href = `/api/sessions/${encodeURIComponent(sessionId)}/export`;
            }

            function isJsonlSessionFile(file) {
              const name = (file.name || '').toLowerCase();
              const type = (file.type || '').toLowerCase();
              return name.endsWith('.jsonl') ||
                type === 'application/x-ndjson' ||
                type === 'application/jsonl' ||
                type === 'application/json-lines';
            }

            function firstJsonlLine(text) {
              const normalized = (text || '').replace(/^\uFEFF/, '');
              const index = normalized.indexOf('\n');
              return (index >= 0 ? normalized.slice(0, index) : normalized).trim();
            }

            function isCodingAgentJsonlContent(text) {
              try {
                const header = JSON.parse(firstJsonlLine(text));
                return header &&
                  header.type === 'session' &&
                  header.source !== 'tau-webui' &&
                  typeof header.cwd === 'string' &&
                  header.cwd.trim().length > 0;
              } catch {
                return false;
              }
            }

            function getCodingAgentJsonlImportCurrentBranchOnly() {
              return document.getElementById('import-current-branch-only')?.checked ?? false;
            }

            function codingAgentJsonlImportQuery(currentBranchOnly) {
              return `?currentBranchOnly=${currentBranchOnly ? 'true' : 'false'}`;
            }

            async function postJsonlImport(url, text) {
              return await fetchJson(url, {
                method: 'POST',
                headers: { 'Content-Type': 'application/x-ndjson' },
                body: text
              });
            }

            async function previewCodingAgentJsonlImport(text, currentBranchOnly) {
              return await postJsonlImport(`/api/sessions/import.coding-agent-jsonl/preview${codingAgentJsonlImportQuery(currentBranchOnly)}`, text);
            }

            async function importCodingAgentJsonlSession(text, preview, currentBranchOnly) {
              const imported = await postJsonlImport(`/api/sessions/import.coding-agent-jsonl${codingAgentJsonlImportQuery(currentBranchOnly)}`, text);
              return preview ? { ...imported, preview } : imported;
            }

            async function importJsonlSession(text) {
              const currentBranchOnly = getCodingAgentJsonlImportCurrentBranchOnly();
              if (isCodingAgentJsonlContent(text)) {
                const preview = await previewCodingAgentJsonlImport(text, currentBranchOnly);
                const notice = createCodingAgentPreviewNotice(preview);
                if (notice) {
                  document.getElementById('session-meta').textContent = notice;
                }
                return await importCodingAgentJsonlSession(text, preview, currentBranchOnly);
              }

              try {
                return await postJsonlImport('/api/sessions/import.jsonl', text);
              } catch (webUiError) {
                let preview = null;
                try {
                  preview = await previewCodingAgentJsonlImport(text, currentBranchOnly);
                  const notice = createCodingAgentPreviewNotice(preview);
                  if (notice) {
                    document.getElementById('session-meta').textContent = notice;
                  }
                } catch {
                  // The import endpoint will return the user-facing parse error below.
                }

                try {
                  return await importCodingAgentJsonlSession(text, preview, currentBranchOnly);
                } catch (codingAgentError) {
                  throw new Error(`WebUi JSONL import failed: ${webUiError.message || webUiError}; CodingAgent JSONL import failed: ${codingAgentError.message || codingAgentError}`);
                }
              }
            }

            function safeCount(value, fallback) {
              const count = Number(value);
              return Number.isFinite(count) && count >= 0 ? count : fallback;
            }

            function pluralize(count, singular, plural) {
              return `${count} ${count === 1 ? singular : (plural || `${singular}s`)}`;
            }

            function createImportNotice(importedResponse, imported) {
              const summary = importedResponse?.summary;
              const preview = importedResponse?.preview;
              const messages = Array.isArray(imported?.messages) ? imported.messages.length : 0;
              const sourceMetadata = imported?.sourceMetadata || importedResponse?.sourceMetadata;
              const sourceStrategy = importedResponse?.sourceStrategy || sourceMetadata?.importStrategy || preview?.importStrategy;
              const sourceAudit = importedResponse?.sourceAudit || sourceMetadata?.audit || preview?.audit;
              const sourceSummary = summary ? {
                importedMessageCount: safeCount(summary.importedMessageCount, messages),
                sourceEntryCount: safeCount(summary.sourceEntryCount, 0),
                sourceMessageCount: safeCount(summary.sourceMessageCount, 0),
                warningCount: safeCount(summary.warningCount, Array.isArray(importedResponse?.warnings) ? importedResponse.warnings.length : 0),
                currentBranchOnly: summary.currentBranchOnly
              } : preview ? {
                importedMessageCount: safeCount(preview.audit?.importedMessageCount, messages),
                sourceEntryCount: safeCount(preview.entryCount, 0),
                sourceMessageCount: safeCount(preview.messageCount, messages),
                warningCount: safeCount(preview.audit?.warnings?.length, 0),
                currentBranchOnly: preview.audit?.willImportCurrentBranchOnly
              } : null;
              const sourceNotice = createCodingAgentSourceMetadataNotice(
                sourceMetadata,
                summary ? safeCount(summary.importedMessageCount, messages) : messages,
                sourceSummary,
                sourceStrategy,
                sourceAudit);
              if (sourceNotice) {
                return sourceNotice;
              }

              const title = imported?.title || imported?.id || 'session';
              return `imported ${title}: ${pluralize(messages, 'message')}`;
            }

            async function deleteSession(sessionId) {
              const response = await fetch(`/api/sessions/${encodeURIComponent(sessionId)}`, { method: 'DELETE' });
              if (!response.ok && response.status !== 404) throw new Error(await response.text());
              if (currentSessionId === sessionId) {
                currentSessionId = null;
                currentSession = null;
                startJavaScriptReplBridgeLoop(null);
                rememberCurrentSession(null);
                applySessionSettings(null);
                renderMessages(null);
                await loadArtifacts(null);
              }
              await loadStatus();
              await loadSessions();
            }

            async function importSessionFile(file) {
              const text = await file.text();
              const importedResponse = isJsonlSessionFile(file)
                ? await importJsonlSession(text)
                : await fetchJson('/api/sessions/import', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify(JSON.parse(text))
                  });
              const imported = importedResponse.session || importedResponse;
              currentSessionId = imported.id;
              rememberCurrentSession(imported.id);
              await loadStatus();
              await loadSessions();
              await openSession(imported.id);
              document.getElementById('session-meta').textContent = createImportNotice(importedResponse, imported);
            }

            function renderSessions(sessions) {
              const root = document.getElementById('sessions');
              root.innerHTML = sessions.map(session => `
                <div class="session ${session.id === currentSessionId ? 'active' : ''}" data-id="${session.id}">
                  <div class="session-title"><strong>${escapeHtml(session.title)}</strong><span class="badge">${escapeHtml(session.provider)}</span></div>
                  <div class="meta">${escapeHtml(session.model)}\n${session.messages.length} messages</div>
                  <div class="session-actions">
                    <button class="secondary" data-action="export" data-id="${escapeAttribute(session.id)}">Export</button>
                    <button class="secondary" data-action="delete" data-id="${escapeAttribute(session.id)}">Delete</button>
                  </div>
                </div>`).join('');
              root.querySelectorAll('.session').forEach(node => node.addEventListener('click', () => openSession(node.dataset.id)));
              root.querySelectorAll('.session-actions button').forEach(node => {
                node.addEventListener('click', async event => {
                  event.stopPropagation();
                  const id = node.dataset.id;
                  if (!id) return;
                  if (node.dataset.action === 'export') {
                    exportSession(id);
                  } else if (node.dataset.action === 'delete') {
                    try {
                      await deleteSession(id);
                    } catch (error) {
                      document.getElementById('session-meta').textContent = `delete failed: ${error.message || error}`;
                    }
                  }
                });
              });
            }

            async function loadStatus() {
              const status = await fetchJson('/api/status');
              document.getElementById('status').textContent = `provider=${status.defaultProvider} | model=${status.defaultModel} | sessions=${status.sessionCount} | persisted=${status.persistenceEnabled}\nstore=${status.sessionsPath}`;
            }

            async function loadCatalog() {
              catalog = await fetchJson('/api/catalog');
              const providerSelect = document.getElementById('provider');
              providerSelect.innerHTML = catalog.providers.map(provider => `<option value="${provider.id}">${escapeHtml(provider.id)}</option>`).join('');
              providerSelect.onchange = () => {
                bindModels(providerSelect.value);
                refreshAuthStatus(providerSelect.value, document.getElementById('model').value);
              };
              document.getElementById('model').onchange = () => {
                refreshAuthStatus(providerSelect.value, document.getElementById('model').value);
              };
              if (catalog.providers.length) {
                providerSelect.value = catalog.providers[0].id;
                bindModels(providerSelect.value);
                refreshAuthStatus(providerSelect.value, document.getElementById('model').value);
              }
            }

            async function loadSessions() {
              const sessions = await fetchJson('/api/sessions');
              renderSessions(sessions);
              const sessionIds = new Set(sessions.map(session => session.id));
              if (currentSessionId && sessionIds.has(currentSessionId)) {
                await openSession(currentSessionId, sessions);
                return;
              }

              const remembered = readRememberedSession();
              if (remembered && sessionIds.has(remembered)) {
                await openSession(remembered);
              } else if (sessions.length) {
                await openSession(sessions[0].id);
              } else {
                currentSessionId = null;
                currentSession = null;
                startJavaScriptReplBridgeLoop(null);
                rememberCurrentSession(null);
                applySessionSettings(null);
                renderMessages(null);
                await loadArtifacts(null);
              }
            }

            async function openSession(id, knownSessions) {
              currentSessionId = id;
              startJavaScriptReplBridgeLoop(id);
              rememberCurrentSession(id);
              const session = await fetchJson(`/api/sessions/${id}`);
              currentSession = session;
              renderSessions(knownSessions || await fetchJson('/api/sessions'));
              applySessionSettings(session);
              renderMessages(session);
              await loadArtifacts(id);
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
              currentSession = session;
              startJavaScriptReplBridgeLoop(session.id);
              rememberCurrentSession(session.id);
              await loadStatus();
              await loadSessions();
              applySessionSettings(session);
              renderMessages(session);
              await loadArtifacts(session.id);
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
              currentSession = session;
              await loadStatus();
              await loadSessions();
              applySessionSettings(session);
              await loadArtifacts(currentSessionId);
            }

            async function sendMessage() {
              if (!currentSessionId) await createSession();
              const prompt = document.getElementById('prompt');
              const sendButton = document.getElementById('send');
              const text = prompt.value.trim();
              const attachments = [...pendingAttachments];
              if (!text && !attachments.length) return;
              prompt.value = '';
              sendButton.disabled = true;
              try {
                const session = await sendMessageStreaming(currentSessionId, text, attachments);
                currentSession = session;
                pendingAttachments = [];
                document.getElementById('attachment-input').value = '';
                renderPendingAttachments();
                await loadStatus();
                renderMessages(session);
                applySessionSettings(session);
                await loadArtifacts(currentSessionId);
                await loadSessions();
              } catch (error) {
                document.getElementById('session-meta').textContent = `send failed: ${error.message || error}`;
              } finally {
                sendButton.disabled = false;
              }
            }

            document.getElementById('new-session').addEventListener('click', createSession);
            document.getElementById('import-session').addEventListener('click', () => document.getElementById('session-import-input').click());
            document.getElementById('session-import-input').addEventListener('change', async event => {
              const file = event.target.files?.[0];
              if (!file) return;
              try {
                await importSessionFile(file);
              } catch (error) {
                document.getElementById('session-meta').textContent = `import failed: ${error.message || error}`;
              } finally {
                event.target.value = '';
              }
            });
            document.getElementById('refresh').addEventListener('click', async () => {
              await loadStatus();
              await loadCatalog();
              if (currentSessionId) {
                await openSession(currentSessionId);
              } else {
                await loadSessions();
              }
            });
            document.getElementById('refresh-artifacts').addEventListener('click', async () => {
              try {
                await loadArtifacts(currentSessionId);
              } catch (error) {
                const preview = document.getElementById('artifact-preview');
                preview.className = 'artifact-empty';
                preview.textContent = `artifact refresh failed: ${error.message || error}`;
              }
            });
            document.getElementById('save-settings').addEventListener('click', saveSettings);
            document.getElementById('attach').addEventListener('click', () => document.getElementById('attachment-input').click());
            document.getElementById('attachment-input').addEventListener('change', async event => {
              const files = [...(event.target.files || [])];
              if (!files.length) return;
              const attachButton = document.getElementById('attach');
              attachButton.disabled = true;
              try {
                const loaded = await Promise.all(files.map(readAttachment));
                pendingAttachments = [...pendingAttachments, ...loaded];
                renderPendingAttachments();
              } catch (error) {
                document.getElementById('session-meta').textContent = `attachment failed: ${error.message || error}`;
              } finally {
                attachButton.disabled = false;
                event.target.value = '';
              }
            });
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
