// Experimental transport: no Blazor/SignalR or .NET callbacks on the upload path.
const managers = new Map();
const customViews = new WeakMap();
const terminal = new Set(['complete', 'cancelled']);
const digest = async blob => Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256', await blob.arrayBuffer())))
    .map(n => n.toString(16).padStart(2, '0')).join('');

function database() {
    return new Promise((resolve, reject) => {
        const request = indexedDB.open('dnax-upload-prototype-v1', 1);
        request.onupgradeneeded = () => request.result.createObjectStore('jobs', { keyPath: 'key' });
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error);
        request.onblocked = () => reject(new Error('Browser storage is blocked.'));
    });
}
async function storage(action, value) {
    const db = await database();
    try {
        return await new Promise((resolve, reject) => {
            const tx = db.transaction('jobs', action === 'all' ? 'readonly' : 'readwrite');
            const store = tx.objectStore('jobs');
            const request = action === 'all' ? store.getAll() : action === 'put' ? store.put(value) : store.delete(value);
            tx.oncomplete = () => resolve(request.result);
            tx.onerror = () => reject(tx.error); tx.onabort = () => reject(tx.error);
        });
    } finally { db.close(); }
}

class UploadManager {
    constructor(endpoint, profile) {
        this.endpoint = endpoint; this.profile = profile; this.jobs = new Map(); this.views = new Set();
        this.busy = 0; this.persisting = 0; this.picker = false; this.storageError = '';
        this.ready = this.initialize();
        window.addEventListener('online', () => this.pump());
        document.addEventListener('visibilitychange', () => { if (!document.hidden) this.pump(); });
    }
    async capabilities() {
        const response = await fetch(`${this.endpoint}/capabilities`, { credentials: 'same-origin', cache: 'no-store' });
        if (!response.ok) throw new Error('Sign in to use uploads.');
        const caps = await response.json();
        if (this.scope && caps.ownerScope !== this.scope) throw new Error('Upload owner changed. Return to the original account.');
        this.scope = caps.ownerScope; this.token = caps.token; this.features = caps.profiles[this.profile];
        if (!this.features) throw new Error('Upload profile is unavailable.');
    }
    async initialize() {
        await this.capabilities();
        this.prefix = `${this.endpoint}|${this.profile}|${this.scope}|`;
        if (this.features.persistMetadata) {
            try {
                for (const saved of await storage('all')) {
                    if (!saved.key.startsWith(this.prefix)) continue;
                    const { blob, ...metadata } = saved;
                    this.jobs.set(saved.id, { ...metadata, file: blob || null, running: false,
                        needsVerification: !!saved.blob, state: terminal.has(saved.state) ? saved.state :
                            saved.state === 'paused' || !saved.features.resume ? 'paused' : saved.blob ? 'queued' : 'reselect' });
                    // Receipt recovery needs no original file and remains available without byte resumption.
                    if (saved.created && !terminal.has(saved.state)) {
                        const job = this.jobs.get(saved.id);
                        try {
                            const status = await this.request(`/sessions/${saved.id}`);
                            if (status.state === 'complete') await this.completed(job, status);
                            else if (status.state === 'cancelled') { job.state = 'cancelled'; job.file = null; await this.save(job); }
                        } catch (error) { job.error = error.message; }
                    }
                }
            } catch { this.storageError = 'Browser recovery storage is unavailable.'; }
        }
        this.render(); this.pump();
    }
    async request(path, init = {}) {
        const response = await fetch(this.endpoint + path, { ...init, credentials: 'same-origin', cache: 'no-store',
            headers: { 'X-DnaX-Antiforgery': this.token, ...init.headers } });
        if (!response.ok) {
            let message = `Upload request failed (${response.status}).`;
            try { message = (await response.json()).error || message; } catch { /* no JSON */ }
            const error = new Error(message); error.status = response.status; throw error;
        }
        if (response.status === 202) { const error = new Error('Upload is still processing.'); error.status = 202; throw error; }
        return response.status === 204 ? null : response.json();
    }
    async save(job) {
        if (!job.features.persistMetadata) return;
        this.persisting++;
        try {
            const { file, controller, running, needsVerification, task, ...record } = job;
            const blob = !terminal.has(job.state) && file && file.size <= job.features.persistFileBytes ? file : null;
            await storage('put', { ...record, key: this.prefix + job.id, blob });
            job.persistedFile = !!blob;
        } catch { job.persistedFile = false; this.storageError = 'Recovery storage failed; keep this page open or reselect after reload.'; }
        finally { this.persisting--; this.render(); }
    }
    async add(files) {
        const f = this.features;
        const selected = Array.from(files); // Caller captures references before async work.
        if ((!f.multiple && selected.length > 1) || selected.length + this.jobs.size > f.maximumFiles) {
            this.storageError = `This profile allows ${f.multiple ? f.maximumFiles : 1} selected file(s).`; this.render(); return;
        }
        // Register the complete batch before the first persistence await.
        const batch = crypto.randomUUID();
        const jobs = selected.map(file => {
            const job = { id: crypto.randomUUID(), batch, profile: this.profile, name: file.name, length: file.size,
                modified: file.lastModified, offset: 0, file, features: { ...f }, state: 'saving', attempts: 0,
                created: false, persistedFile: false, error: '', needsVerification: false };
            if (!Number.isSafeInteger(file.size) || file.size > f.maximumFileBytes) {
                job.state = 'failed'; job.error = 'File exceeds the configured size limit.'; job.file = null;
            }
            this.jobs.set(job.id, job); return job;
        });
        this.render();
        // Sequential persistence bounds concurrent Blob copies.
        for (const job of jobs) { await this.save(job); if (job.state === 'saving') job.state = 'queued'; }
        this.pump();
    }
    pump() {
        if (!this.features || !navigator.onLine) return;
        for (const job of this.jobs.values()) {
            if (this.busy >= this.features.concurrentFiles) break;
            if (job.state !== 'queued' || job.running || !job.file) continue;
            this.busy++; job.running = true;
            job.task = this.run(job).finally(() => { job.running = false; this.busy--; this.render(); this.pump(); });
        }
    }
    async run(job) {
        job.controller = new AbortController(); const signal = job.controller.signal;
        const path = `/sessions/${job.id}`;
        try {
            job.state = 'uploading'; job.error = ''; this.render();
            await this.capabilities(); // Refresh antiforgery without a circuit.
            let status = await this.request('/sessions', { method: 'POST', signal, headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ id: job.id, profile: job.profile, fileName: job.name, length: job.length }) });
            job.created = true; job.features = status.features; job.offset = status.offset;
            if (status.state === 'cancelled') { job.state = 'cancelled'; await this.save(job); return; }
            if (status.state === 'complete') { await this.completed(job, status); return; }
            if (!job.features.resume && status.offset > 0) {
                status = await this.request(`${path}/restart`, { method: 'POST', signal });
                job.offset = 0;
            }
            if (job.needsVerification && status.offset > 0) {
                job.state = 'verifying'; this.render();
                for (let offset = 0; offset < status.offset; offset += job.features.chunkBytes) {
                    const end = Math.min(status.offset, offset + job.features.chunkBytes);
                    const sha256 = await digest(job.file.slice(offset, end));
                    await this.request(`${path}/verify`, { method: 'POST', signal, headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ offset, length: end - offset, sha256 }) });
                }
            }
            job.needsVerification = false; job.state = 'uploading';
            // Whole-file mode is one Blob request, never an ArrayBuffer or a multipart model-bound request.
            if (!job.features.chunking && status.offset !== job.length) {
                status = await this.request(`${path}/bytes`, { method: 'PUT', signal,
                    headers: { 'Content-Type': 'application/octet-stream', 'Upload-Offset': '0' }, body: job.file });
                job.offset = status.offset; await this.save(job);
            } else if (job.features.chunking) {
                while (status.offset < job.length) {
                    const end = Math.min(job.length, status.offset + job.features.chunkBytes);
                    const blob = job.file.slice(status.offset, end);
                    const sha256 = await digest(blob);
                    status = await this.request(`${path}/bytes`, { method: 'PUT', signal,
                        headers: { 'Content-Type': 'application/octet-stream', 'Upload-Offset': String(status.offset), 'Upload-SHA256': sha256 }, body: blob });
                    job.offset = status.offset;
                    if (job.offset > (job.highWaterMark || 0)) { job.highWaterMark = job.offset; job.attempts = 0; }
                    await this.save(job);
                }
            }
            if (job.length === 0) await this.request(`${path}/bytes`, { method: 'PUT', signal,
                headers: { 'Content-Type': 'application/octet-stream', 'Upload-Offset': '0',
                    ...(job.features.chunking ? { 'Upload-SHA256': await digest(job.file) } : {}) }, body: job.file });
            job.state = 'completing'; this.render();
            status = await this.request(`${path}/complete`, { method: 'POST', signal });
            await this.completed(job, status);
        } catch (error) {
            if (job.state === 'paused' || job.state === 'cancelling') return;
            job.error = error.message;
            if (error.status === 422 && job.needsVerification) job.file = null;
            const retryable = !error.status || [202, 409, 429].includes(error.status) || error.status >= 500;
            if (retryable && job.features.automaticRetry && job.attempts < job.features.retryLimit) {
                job.state = 'waiting'; const delay = Math.min(30000, 1000 * 2 ** job.attempts++);
                setTimeout(() => { if (job.state === 'waiting') { job.state = 'queued'; this.pump(); } }, delay);
            } else job.state = job.file ? 'failed' : 'reselect';
            await this.save(job);
        }
    }
    async completed(job, status) {
        if (status.state !== 'complete' || status.offset !== job.length) throw new Error('Completion receipt is invalid.');
        job.state = 'complete'; job.offset = status.offset; job.file = null; job.error = ''; await this.save(job);
        for (const view of this.views) view.dispatchEvent(new CustomEvent('dnax-upload-complete', { bubbles: true, detail: status }));
    }
    async pause(job) {
        if (terminal.has(job.state)) return;
        job.state = 'paused'; job.controller?.abort(); await this.save(job);
    }
    async resume(job) {
        if (terminal.has(job.state) || job.running) return;
        if (!job.features.resume && job.created) { job.error = 'Resuming is disabled. Cancel and select again.'; this.render(); return; }
        job.state = job.file ? 'queued' : 'reselect'; job.attempts = 0; await this.save(job); this.pump();
    }
    async cancel(job) {
        if (terminal.has(job.state)) return;
        job.state = 'cancelling'; job.controller?.abort(); this.render();
        try {
            await job.task;
            await this.capabilities();
            try { await this.request(`/sessions/${job.id}`, { method: 'DELETE' }); }
            catch (error) { if (error.status !== 404) throw error; }
            job.state = 'cancelled'; job.file = null; await this.save(job);
        } catch (error) { job.state = 'failed'; job.error = error.message; this.render(); }
    }
    async reselect(job, file) {
        if (file.size !== job.length) { job.error = 'Select the original file with the same size.'; this.render(); return; }
        job.file = file; job.needsVerification = true; await this.resume(job);
    }
    attach(view) {
        this.views.add(view); this.render();
        return this.ready.then(() => this.render()).catch(error => {
            if (customViews.has(view)) { this.storageError = error.message; this.render(); }
            else view.textContent = error.message;
        });
    }
    render() {
        // Do not replace a native file input while its OS picker owns the selection.
        if (this.picker) return;
        for (const view of this.views) {
            if (!view.isConnected) { this.views.delete(view); continue; }
            const custom = customViews.get(view);
            if (custom) {
                const snapshot = { features: this.features ? { ...this.features } : null, error: this.storageError,
                    jobs: [...this.jobs.values()].map(job => ({ id: job.id, name: job.name, length: job.length,
                        offset: job.offset, state: job.state, error: job.error || '', hasFile: !!job.file,
                        persistedFile: !!job.persistedFile, features: { ...job.features } })) };
                // Presentation failures must not abort accepted-byte recovery or the transfer queue.
                try { custom.render(snapshot, custom.actions); }
                catch (error) { console.error('Upload view render failed.', error); }
                continue;
            }
            view.replaceChildren();
            const f = this.features; if (!f) { view.textContent = 'Loading upload settings…'; continue; }
            const input = document.createElement('input'); input.type = 'file'; input.multiple = f.multiple;
            input.setAttribute('aria-label', 'Choose files to upload');
            input.addEventListener('click', () => { this.picker = true; });
            input.addEventListener('cancel', () => { this.picker = false; });
            input.addEventListener('change', () => { const files = Array.from(input.files || []); this.picker = false; void this.add(files); });
            view.append(input);
            if (f.dragAndDrop) {
                view.ondragover = event => event.preventDefault();
                view.ondrop = event => { event.preventDefault(); const files = Array.from(event.dataTransfer.files); void this.add(files); };
            }
            const button = (label, action, parent = view) => { const b = document.createElement('button'); b.type = 'button'; b.textContent = label;
                b.addEventListener('click', () => { void action(); }); parent.append(b); };
            if (f.showPause && f.resume) { button('Pause all', () => Promise.all([...this.jobs.values()].map(j => this.pause(j))));
                button('Resume all', () => Promise.all([...this.jobs.values()].map(j => this.resume(j)))); }
            if (f.showCancel) button('Cancel pending', () => Promise.all([...this.jobs.values()].map(j => this.cancel(j))));
            const summary = document.createElement('p');
            const jobs = [...this.jobs.values()];
            // BigInt keeps aggregate totals exact even when many individually safe lengths are queued.
            const accepted = jobs.reduce((total, job) => total + BigInt(job.offset), 0n);
            const total = jobs.reduce((sum, job) => sum + BigInt(job.length), 0n);
            summary.textContent = `${jobs.filter(j => j.state === 'complete').length}/${jobs.length} files complete. ` +
                `${accepted.toLocaleString()}/${total.toLocaleString()} bytes acknowledged. ${this.storageError}`;
            view.append(summary);
            for (const job of jobs) {
                const row = document.createElement('div'); row.className = 'dnax-upload-row'; row.dataset.uploadId = job.id;
                const label = document.createElement('p');
                label.textContent = `${job.name} — ${job.state} — ${job.offset.toLocaleString()}/${job.length.toLocaleString()} bytes. ${job.error || ''}`;
                row.append(label);
                const progress = document.createElement('progress'); progress.max = job.length || 1; progress.value = job.offset;
                progress.setAttribute('aria-label', `Acknowledged bytes for ${job.name}`); row.append(progress);
                if (!terminal.has(job.state)) {
                    if (job.features.showPause && job.features.resume) {
                        button('Pause', () => this.pause(job), row); button('Resume / retry', () => this.resume(job), row);
                    }
                    if (job.features.showCancel) button('Cancel', () => this.cancel(job), row);
                    if (!job.file && job.features.resume) {
                        const pick = document.createElement('input'); pick.type = 'file'; pick.setAttribute('aria-label', `Reselect ${job.name}`);
                        pick.addEventListener('click', () => { this.picker = true; }); pick.addEventListener('cancel', () => { this.picker = false; });
                        pick.addEventListener('change', () => { this.picker = false; const file = pick.files[0]; if (file) void this.reselect(job, file); }); row.append(pick);
                    }
                    const recovery = document.createElement('small'); recovery.textContent = job.persistedFile ? 'File saved for reload recovery.' :
                        'After reload, the original file may need to be selected again.'; row.append(recovery);
                } else button('Dismiss', async () => { this.jobs.delete(job.id); if (job.features.persistMetadata) await storage('delete', this.prefix + job.id); this.render(); }, row);
                view.append(row);
            }
        }
    }
}

function managerFor(endpoint, profile) {
    endpoint = new URL(endpoint, document.baseURI).href.replace(/\/$/, '');
    if (new URL(endpoint).origin !== location.origin) throw new Error('Uploads must use a same-origin endpoint.');
    const key = `${endpoint}|${profile}`;
    if (!managers.has(key)) managers.set(key, new UploadManager(endpoint, profile));
    return managers.get(key);
}

export function mount(view, endpoint, profile) {
    return managerFor(endpoint, profile).attach(view);
}

// Native applications can preserve their own picker, progress markup and accessibility behavior.
// The synchronous renderer receives copied state and browser-only actions; no File objects or .NET calls.
export function mountCustom(view, endpoint, profile, render) {
    if (typeof render !== 'function') throw new TypeError('A synchronous upload renderer is required.');
    const manager = managerFor(endpoint, profile);
    const jobFor = id => { const job = manager.jobs.get(id); if (!job) throw new Error('Upload is unavailable.'); return job; };
    const actions = {
        addFiles: files => { const captured = Array.from(files); manager.picker = false; return manager.add(captured); },
        setPickerOpen: open => { manager.picker = !!open; if (!open) manager.render(); },
        pause: id => manager.pause(jobFor(id)),
        resume: id => manager.resume(jobFor(id)),
        cancel: id => manager.cancel(jobFor(id)),
        reselect: (id, file) => { manager.picker = false; return manager.reselect(jobFor(id), file); },
        dismiss: async id => {
            const job = jobFor(id); if (!terminal.has(job.state)) return;
            manager.jobs.delete(id);
            if (job.features.persistMetadata) await storage('delete', manager.prefix + id);
            manager.render();
        }
    };
    customViews.set(view, { render, actions });
    return manager.attach(view);
}

// The host should call this before an application-controlled full reload.
export function canReloadSafely() {
    return [...managers.values()].every(m => !m.picker && m.persisting === 0 &&
        [...m.jobs.values()].every(j => terminal.has(j.state) || j.persistedFile));
}
