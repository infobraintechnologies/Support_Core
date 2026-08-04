(function (global) {
    "use strict";

    const terminalStates = new Set(["Ready", "Rejected", "ScanFailed", "Deleted", "Expired"]);
    const accepted = ".pdf,.jpg,.jpeg,.png,.docx,.xlsx";

    async function parse(response) {
        const type = response.headers.get("content-type") || "";
        const body = type.includes("json") ? await response.json() : null;
        if (!response.ok) {
            const error = new Error(body?.detail || body?.title || `Attachment request failed (${response.status}).`);
            error.status = response.status;
            error.problem = body;
            throw error;
        }
        return body;
    }

    function createComposer(options) {
        const items = new Map();
        let destroyed = false;
        const input = options.input;
        const button = options.button;
        const list = options.list;
        if (!input || !button || !list) return null;
        input.accept = accepted;
        input.multiple = true;

        button.addEventListener("click", () => input.click());
        input.addEventListener("change", () => {
            const files = Array.from(input.files || []);
            input.value = "";
            addFiles(files);
        });
        global.addEventListener("pagehide", handlePageHide);

        async function addFiles(files) {
            const conversationId = Number(options.getConversationId?.());
            if (!Number.isSafeInteger(conversationId) || conversationId <= 0) return;
            const selectedItems = Array.from(items.values())
                .filter(item => !["Rejected", "ScanFailed", "Deleted", "Expired"].includes(item.status));
            const currentBytes = selectedItems
                .reduce((sum, item) => sum + Number(item.file?.size || item.size || 0), 0);
            if (selectedItems.length + files.length > 5
                || currentBytes + files.reduce((sum, file) => sum + file.size, 0) > 25 * 1024 * 1024) {
                options.onError?.("A message can include at most 5 files and 25 MiB.");
                return;
            }
            for (const file of files) {
                if (file.size > 10 * 1024 * 1024) {
                    options.onError?.(`${file.name} exceeds 10 MiB.`);
                    continue;
                }
                const localId = global.crypto?.randomUUID?.() || `${Date.now()}-${Math.random()}`;
                const item = {
                    localId,
                    conversationId,
                    file,
                    name: file.name,
                    size: file.size,
                    status: "Creating",
                    progress: 0
                };
                items.set(localId, item);
                render();
                void upload(item, conversationId);
            }
        }

        async function upload(item, conversationId) {
            try {
                const intent = await parse(await fetch(
                    `/api/v1/conversations/${conversationId}/attachment-uploads`,
                    {
                        method: "POST",
                        credentials: "same-origin",
                        headers: { "Content-Type": "application/json" },
                        body: JSON.stringify({
                            displayName: item.file.name,
                            mediaType: item.file.type || mediaTypeFromName(item.file.name),
                            size: item.file.size
                        })
                    }));
                if (destroyed
                    || item.cancelled
                    || items.get(item.localId) !== item
                    || Number(options.getConversationId?.()) !== conversationId) {
                    await cancelRemote(intent.id, false);
                    return;
                }
                item.id = intent.id;
                item.status = "Uploading";
                item.intent = intent;
                await put(item, intent);
                if (destroyed || item.cancelled || items.get(item.localId) !== item) {
                    await cancelRemote(intent.id, false);
                    return;
                }
                item.status = "Uploaded";
                render();
                const completion = await parse(await fetch(`/api/v1/attachments/${intent.id}/complete`, {
                    method: "POST",
                    credentials: "same-origin"
                }));
                item.status = completion?.status || "Uploaded";
                render();
                await poll(item);
            } catch (error) {
                if (item.cancelled || destroyed) return;
                item.status = "Failed";
                item.error = error.message;
                render();
                notify();
            }
        }

        function put(item, intent) {
            return new Promise((resolve, reject) => {
                if (destroyed || item.cancelled) {
                    reject(new DOMException("Upload cancelled.", "AbortError"));
                    return;
                }
                const xhr = new XMLHttpRequest();
                item.xhr = xhr;
                xhr.open("PUT", intent.uploadUrl, true);
                for (const [name, value] of Object.entries(intent.requiredHeaders || {})) {
                    xhr.setRequestHeader(name, value);
                }
                xhr.upload.addEventListener("progress", event => {
                    if (event.lengthComputable) {
                        item.progress = Math.round(event.loaded * 100 / event.total);
                        render();
                    }
                });
                xhr.addEventListener("load", () => {
                    const etag = xhr.getResponseHeader("ETag");
                    if (xhr.status >= 200 && xhr.status < 300 && etag) resolve();
                    else reject(new Error("The direct upload did not complete."));
                });
                xhr.addEventListener("error", () => reject(new Error("The direct upload failed.")));
                xhr.addEventListener("abort", () => reject(new DOMException("Upload cancelled.", "AbortError")));
                xhr.send(item.file);
            });
        }

        async function poll(item, manual = false) {
            const started = Date.now();
            while (!destroyed && !item.cancelled && item.id) {
                const status = await parse(await fetch(`/api/v1/attachments/${item.id}`, {
                    credentials: "same-origin"
                }));
                item.status = status.status;
                item.error = status.rejectionCode || null;
                item.mediaType = status.mediaType;
                item.size = status.size;
                render();
                notify();
                if (terminalStates.has(status.status)) return;
                const elapsed = Date.now() - started;
                if (elapsed >= 5 * 60 * 1000) {
                    item.status = "TimedOut";
                    render();
                    notify();
                    return;
                }
                await delay(elapsed < 30000 ? 2000 : 5000);
                if (manual) manual = false;
            }
        }

        async function cancel(item) {
            item.cancelled = true;
            item.xhr?.abort();
            const attachmentId = item.id;
            items.delete(item.localId);
            render();
            notify();
            if (attachmentId) await cancelRemote(attachmentId, true);
        }

        async function retry(item) {
            const previousId = item.id;
            item.cancelled = false;
            item.error = null;
            item.id = null;
            item.progress = 0;
            item.status = "Creating";
            render();
            if (previousId) await cancelRemote(previousId, true);
            if (destroyed || item.cancelled || items.get(item.localId) !== item) return;
            const conversationId = Number(options.getConversationId?.());
            if (!Number.isSafeInteger(conversationId)
                || conversationId <= 0
                || conversationId !== item.conversationId) {
                remove(item);
                return;
            }
            void upload(item, conversationId);
        }

        function remove(item) {
            item.cancelled = true;
            item.xhr?.abort();
            items.delete(item.localId);
            render();
            notify();
        }

        async function checkStatus(item) {
            try {
                await poll(item, true);
            } catch (error) {
                if (item.cancelled || destroyed) return;
                item.status = "Failed";
                item.error = error.message;
                render();
                notify();
            }
        }

        async function cancelRemote(attachmentId, reportError) {
            try {
                await parse(await fetch(`/api/v1/attachments/${attachmentId}`, {
                    method: "DELETE",
                    credentials: "same-origin",
                    keepalive: true
                }));
            } catch (error) {
                if (reportError) options.onError?.(error.message);
            }
        }

        function render() {
            list.replaceChildren();
            for (const item of items.values()) {
                const row = document.createElement("div");
                row.className = "attachment-upload-item";
                const copy = document.createElement("span");
                copy.className = "attachment-upload-copy";
                const name = document.createElement("strong");
                name.textContent = item.name;
                const state = document.createElement("small");
                state.textContent = item.status === "Uploading"
                    ? `Uploading ${item.progress}%`
                    : item.error ? `${item.status}: ${item.error}` : item.status;
                copy.append(name, state);
                const actions = document.createElement("span");
                actions.className = "attachment-upload-actions";
                if (item.status === "Failed") actions.append(action("Retry", () => retry(item)));
                if (item.status === "TimedOut") {
                    actions.append(action("Check status", () => checkStatus(item)));
                }
                if (!terminalStates.has(item.status) || item.status === "Ready") {
                    actions.append(action("Cancel", () => cancel(item)));
                } else {
                    actions.append(action("Remove", () => remove(item)));
                }
                row.append(copy, actions);
                list.appendChild(row);
            }
        }

        function action(label, handler) {
            const control = document.createElement("button");
            control.type = "button";
            control.className = "btn btn-link btn-sm";
            control.textContent = label;
            control.addEventListener("click", handler);
            return control;
        }

        function notify() {
            options.onReadyChanged?.(getReadyIds(), Array.from(items.values()));
        }

        function getReadyIds() {
            return Array.from(items.values())
                .filter(item => item.status === "Ready" && item.id)
                .map(item => item.id);
        }

        function clearBound(ids) {
            const sent = new Set((ids || []).map(String));
            for (const [key, item] of items) {
                if (sent.has(String(item.id))) items.delete(key);
            }
            render();
            notify();
        }

        function resetForConversation() {
            const abandoned = Array.from(items.values());
            for (const item of abandoned) {
                item.cancelled = true;
                item.xhr?.abort();
            }
            items.clear();
            render();
            notify();
            return Promise.allSettled(
                abandoned
                    .filter(item => item.id)
                    .map(item => cancelRemote(item.id, false)));
        }

        function handlePageHide() {
            void resetForConversation();
        }

        function destroy() {
            destroyed = true;
            global.removeEventListener("pagehide", handlePageHide);
            void resetForConversation();
        }

        return { addFiles, getReadyIds, clearBound, resetForConversation, destroy };
    }

    function renderMessageAttachments(container, attachments) {
        if (!container || !Array.isArray(attachments) || attachments.length === 0) return;
        const list = document.createElement("div");
        list.className = "message-attachments";
        for (const attachment of attachments) {
            const href = `/api/v1/attachments/${encodeURIComponent(attachment.id)}/content`;
            if (String(attachment.mediaType || "").startsWith("image/")) {
                const image = document.createElement("img");
                image.src = `${href}?disposition=inline`;
                image.alt = attachment.displayName || "Image attachment";
                image.loading = "lazy";
                list.appendChild(image);
            }
            const link = document.createElement("a");
            link.href = `${href}?disposition=attachment`;
            link.textContent = attachment.displayName || "Download attachment";
            link.rel = "noopener";
            list.appendChild(link);
        }
        container.appendChild(list);
    }

    function mediaTypeFromName(name) {
        const extension = String(name).toLowerCase().split(".").pop();
        return {
            jpg: "image/jpeg", jpeg: "image/jpeg", png: "image/png",
            pdf: "application/pdf",
            docx: "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            xlsx: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
        }[extension] || "application/octet-stream";
    }

    function delay(milliseconds) {
        return new Promise(resolve => global.setTimeout(resolve, milliseconds));
    }

    global.CBSSupportAttachments = { createComposer, renderMessageAttachments };
})(window);
