(function (global) {
    "use strict";

    const namespace = global.CBSSupportMessaging = global.CBSSupportMessaging || {};

    function createClient(options = {}) {
        const store = namespace.createStore(options);
        const api = namespace.createApi(options);
        const transport = namespace.createTransport(options);
        const listeners = new Map();
        const reconciliation = new Map();
        const reconciliationControllers = new Map();
        const lifecycleEventIds = new Set();
        const realtimeOnlyConversationIds = new Set();

        function emit(name, payload) {
            for (const listener of listeners.get(name) || []) listener(payload);
        }

        function on(name, listener) {
            if (!listeners.has(name)) listeners.set(name, new Set());
            listeners.get(name).add(listener);
            return () => listeners.get(name)?.delete(listener);
        }

        function ingest(message, source) {
            const result = store.upsert(message);
            const pending = store.findPending(
                message.conversationId,
                item => String(item.clientMessageId || "").toLowerCase()
                    === String(message.clientMessageId || "").toLowerCase());
            if (pending?.clientMessageId
                && String(pending.clientMessageId).toLowerCase()
                    === String(message.clientMessageId || "").toLowerCase()) {
                store.clearPending(message.conversationId, pending.clientMessageId);
                if (store.loadDraft(message.conversationId) === pending.text) {
                    store.clearDraft(message.conversationId);
                }
                emit("sendstate", {
                    conversationId: Number(message.conversationId),
                    clientMessageId: pending.clientMessageId,
                    state: "sent"
                });
            }
            const summary = store.getConversation(message.conversationId);
            if (summary) {
                summary.latestSequence = Math.max(
                    Number(summary.latestSequence || 0),
                    Number(message.sequence || 0));
                summary.unreadCount = Math.max(
                    summary.latestSequence - Number(summary.lastReadSequence || 0),
                    0);
                emit("conversations", store.listConversations());
            }
            if (result.isNew) emit("message", { message: result.message, source });
            return result;
        }

        async function fetchAfter(conversationId, initialAfterSequence, signal) {
            const id = Number(conversationId);
            let afterSequence = Number(initialAfterSequence || 0);
            let pageCount = 0;
            do {
                const page = await api.getMessages(id, { afterSequence, limit: 100 }, signal);
                for (const message of page.items) ingest(message, "reconcile");
                const nextSequence = store.getLastSequence(id);
                pageCount += 1;
                if (!page.nextCursor || nextSequence <= afterSequence || pageCount >= 100) break;
                afterSequence = nextSequence;
            } while (true);
            return store.getMessages(id);
        }

        async function reconcile(conversationId) {
            const id = Number(conversationId);
            if (reconciliation.has(id)) return reconciliation.get(id);

            const controller = new AbortController();
            reconciliationControllers.set(id, controller);
            const task = (async () => {
                return fetchAfter(id, store.getLastSequence(id), controller.signal);
            })().finally(() => {
                reconciliation.delete(id);
                reconciliationControllers.delete(id);
            });

            reconciliation.set(id, task);
            return task;
        }

        function cancelReconcile(conversationId) {
            reconciliationControllers.get(Number(conversationId))?.abort();
        }

        async function loadOlder(conversationId) {
            const id = Number(conversationId);
            const beforeSequence = store.getFirstSequence(id);
            if (beforeSequence <= 1) return store.getMessages(id);
            const page = await api.getMessages(id, { beforeSequence, limit: 100 });
            for (const message of page.items) ingest(message, "history");
            return store.getMessages(id);
        }

        transport.on("message", message => {
            const id = Number(message?.conversationId);
            const sequence = Number(message?.sequence || 0);
            const lastSequence = store.getLastSequence(id);
            if (sequence > lastSequence + 1) {
                fetchAfter(id, lastSequence)
                    .then(() => ingest(message, "realtime"))
                    .catch(error => {
                        ingest(message, "realtime");
                        emit("error", { operation: "sequence-gap", conversationId: id, error });
                    });
                return;
            }
            ingest(message, "realtime");
        });
        transport.on("typing", typing => emit("typing", typing));
        transport.on("state", state => emit("state", state));
        transport.on("error", error => emit("error", error));
        transport.on("conversationchanged", change => {
            if (change.eventId && lifecycleEventIds.has(change.eventId)) return;
            if (change.eventId) {
                lifecycleEventIds.add(change.eventId);
                if (lifecycleEventIds.size > 1000) {
                    lifecycleEventIds.delete(lifecycleEventIds.values().next().value);
                }
            }
            emit("conversationchanged", change);
        });
        transport.on("rejoined", ids => {
            for (const id of ids) {
                if (realtimeOnlyConversationIds.has(Number(id))) continue;
                reconcile(id).catch(error => emit("error", {
                    operation: "reconcile",
                    conversationId: id,
                    error
                }));
            }
        });

        async function listConversations(query = {}) {
            const collected = [];
            let beforeConversationId = Number(query.beforeConversationId) || null;
            let pageCount = 0;
            do {
                const page = await api.listConversations({
                    limit: query.limit || 100,
                    beforeConversationId
                });
                collected.push(...page.items);
                beforeConversationId = page.nextCursor;
                pageCount += 1;
            } while (beforeConversationId && pageCount < 10 && query.all !== false);

            const conversations = store.replaceConversations(collected);
            emit("conversations", conversations);
            return conversations;
        }

        async function getOrCreateGroup(clientId, signal) {
            const conversation = await api.getOrCreateGroup(clientId, signal);
            store.upsertConversation(conversation);
            emit("conversations", store.listConversations());
            return conversation;
        }

        async function getOrCreatePrivate(counterpartyUserId, signal) {
            const conversation = await api.getOrCreatePrivate(counterpartyUserId, signal);
            store.upsertConversation(conversation);
            emit("conversations", store.listConversations());
            return conversation;
        }

        async function advanceRead(conversationId, throughSequence, signal) {
            const sequence = Number(throughSequence || 0);
            await api.advanceRead(conversationId, sequence, signal);
            const summary = store.markConversationRead(conversationId, sequence);
            emit("read", { conversationId: Number(conversationId), throughSequence: sequence });
            if (summary) emit("conversations", store.listConversations());
        }

        async function transfer(conversationId, request, signal) {
            const conversation = await api.transfer(conversationId, request, signal);
            store.upsertConversation(conversation);
            emit("conversations", store.listConversations());
            return conversation;
        }

        async function archive(conversationId, expectedVersion, signal) {
            const conversation = await api.archive(conversationId, expectedVersion, signal);
            store.removeConversation(conversationId);
            emit("conversations", store.listConversations());
            return conversation;
        }

        async function join(conversationId) {
            realtimeOnlyConversationIds.delete(Number(conversationId));
            await transport.join(conversationId);
            return reconcile(conversationId);
        }

        async function joinRealtime(conversationId) {
            const id = Number(conversationId);
            realtimeOnlyConversationIds.add(id);
            await transport.join(id);
        }

        async function leave(conversationId) {
            const id = Number(conversationId);
            realtimeOnlyConversationIds.delete(id);
            cancelReconcile(id);
            await transport.leave(id);
        }

        async function send(conversationId, text, clientMessageId, attachmentIds = []) {
            const id = Number(conversationId);
            const normalizedText = String(text || "").trim();
            const normalizedAttachmentIds = Array.from(
                new Set((attachmentIds || []).map(String).filter(Boolean)));
            if ((!normalizedText && normalizedAttachmentIds.length === 0)
                || normalizedText.length > 4000
                || normalizedAttachmentIds.length > 5) {
                throw new Error("A message requires text or up to five attachments.");
            }

            const existingPending = store.findPending(
                id,
                item => item.text === normalizedText
                    && JSON.stringify(item.attachmentIds || [])
                        === JSON.stringify(normalizedAttachmentIds)
                    && item.state !== "sent");
            const requestId = clientMessageId
                || (existingPending?.text === normalizedText
                    ? existingPending.clientMessageId
                    : createClientMessageId());
            if (!requestId) throw new Error("This browser cannot create a message identifier.");

            store.saveDraft(id, text);
            store.savePending(id, {
                clientMessageId: requestId,
                text: normalizedText,
                attachmentIds: normalizedAttachmentIds,
                state: "pending"
            });
            emit("sendstate", {
                conversationId: id,
                clientMessageId: requestId,
                text: normalizedText,
                state: "pending"
            });
            try {
                const message = await api.sendMessage(id, {
                    clientMessageId: requestId,
                    text: normalizedText || null,
                    attachmentIds: normalizedAttachmentIds
                });
                const result = ingest(message, "send");
                store.clearPending(id, requestId);
                if (store.loadDraft(id) === String(text || "")) {
                    store.clearDraft(id);
                }
                return result.message;
            } catch (error) {
                store.savePending(id, {
                    clientMessageId: requestId,
                    text: normalizedText,
                    attachmentIds: normalizedAttachmentIds,
                    state: "failed"
                });
                emit("sendstate", {
                    conversationId: id,
                    clientMessageId: requestId,
                    text: normalizedText,
                    state: "failed",
                    error
                });
                throw error;
            }
        }

        async function retry(conversationId, clientMessageId) {
            const id = Number(conversationId);
            const requestId = String(clientMessageId || "");
            const pending = store.findPending(
                id,
                item => String(item.clientMessageId || "").toLowerCase() === requestId.toLowerCase());
            if (!pending) throw new Error("The failed message is no longer available to retry.");
            return send(
                id,
                pending.text,
                pending.clientMessageId,
                pending.attachmentIds || []);
        }

        function createClientMessageId() {
            if (global.crypto?.randomUUID) return global.crypto.randomUUID();
            if (!global.crypto?.getRandomValues) return null;
            const bytes = global.crypto.getRandomValues(new Uint8Array(16));
            bytes[6] = (bytes[6] & 0x0f) | 0x40;
            bytes[8] = (bytes[8] & 0x3f) | 0x80;
            const hex = Array.from(bytes, value => value.toString(16).padStart(2, "0"));
            return `${hex.slice(0, 4).join("")}-${hex.slice(4, 6).join("")}-${hex.slice(6, 8).join("")}-${hex.slice(8, 10).join("")}-${hex.slice(10).join("")}`;
        }

        function reconcileJoinedConversations(operation) {
            for (const id of transport.getJoinedConversationIds()) {
                if (realtimeOnlyConversationIds.has(Number(id))) continue;
                reconcile(id).catch(error => emit("error", {
                    operation,
                    conversationId: id,
                    error
                }));
            }
        }

        global.addEventListener("online", () => {
            transport.start()
                .then(() => reconcileJoinedConversations("online-reconcile"))
                .catch(error => emit("error", { operation: "online-connect", error }));
        });
        global.document?.addEventListener("visibilitychange", () => {
            if (global.document.visibilityState === "visible") {
                reconcileJoinedConversations("visibility-reconcile");
            }
        });
        const reconciliationInterval = Math.max(
            Number(options.reconciliationInterval) || 30000,
            10000);
        global.setInterval(() => {
            if (!global.document || global.document.visibilityState === "visible") {
                reconcileJoinedConversations("periodic-reconcile");
            }
        }, reconciliationInterval);

        return {
            connection: transport.connection,
            start: transport.start,
            join,
            joinRealtime,
            leave,
            setTyping: transport.setTyping,
            reconcile,
            cancelReconcile,
            loadOlder,
            send,
            retry,
            listConversations,
            getOrCreateGroup,
            getAvailableAdmins: api.getAvailableAdmins,
            getAvailableClientUsers: api.getAvailableClientUsers,
            getOrCreatePrivate,
            advanceRead,
            transfer,
            archive,
            on,
            store
        };
    }

    namespace.createClient = createClient;
})(window);
