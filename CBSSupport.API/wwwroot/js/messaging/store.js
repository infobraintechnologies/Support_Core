(function (global) {
    "use strict";

    const namespace = global.CBSSupportMessaging = global.CBSSupportMessaging || {};

    function normalizeConversationId(value) {
        const id = Number(value);
        if (!Number.isSafeInteger(id) || id <= 0) {
            throw new TypeError("A valid conversation ID is required.");
        }

        return id;
    }

    function normalizeMessage(message) {
        if (!message || typeof message !== "object") {
            throw new TypeError("A valid message is required.");
        }

        return {
            ...message,
            id: Number(message.id),
            conversationId: normalizeConversationId(message.conversationId),
            sequence: Number(message.sequence || 0),
            clientMessageId: message.clientMessageId || null
        };
    }

    function createStore(options = {}) {
        const conversations = new Map();
        const conversationSummaries = new Map();
        const draftScope = String(options.draftScope || "anonymous").replace(/[^a-zA-Z0-9:_-]/g, "_");
        const volatileDrafts = new Map();
        const volatilePending = new Map();

        function getState(conversationId) {
            const id = normalizeConversationId(conversationId);
            if (!conversations.has(id)) {
                conversations.set(id, {
                    messages: [],
                    messageIds: new Set(),
                    clientMessageIds: new Set(),
                    lastSequence: 0
                });
            }

            return conversations.get(id);
        }

        function upsert(message) {
            const normalized = normalizeMessage(message);
            const state = getState(normalized.conversationId);
            const idKey = Number.isSafeInteger(normalized.id) && normalized.id > 0
                ? String(normalized.id)
                : null;
            const clientKey = normalized.clientMessageId
                ? String(normalized.clientMessageId).toLowerCase()
                : null;

            if ((idKey && state.messageIds.has(idKey))
                || (clientKey && state.clientMessageIds.has(clientKey))) {
                return { isNew: false, message: normalized };
            }

            if (idKey) state.messageIds.add(idKey);
            if (clientKey) state.clientMessageIds.add(clientKey);
            state.lastSequence = Math.max(state.lastSequence, normalized.sequence);
            state.messages.push(normalized);
            state.messages.sort((left, right) => {
                const sequenceOrder = left.sequence - right.sequence;
                return sequenceOrder || left.id - right.id;
            });

            return { isNew: true, message: normalized };
        }

        function replace(conversationId, messages) {
            const id = normalizeConversationId(conversationId);
            conversations.delete(id);
            for (const message of messages || []) {
                upsert({ ...message, conversationId: message.conversationId || id });
            }
            return getMessages(id);
        }

        function getMessages(conversationId) {
            return getState(conversationId).messages.slice();
        }

        function getLastSequence(conversationId) {
            return getState(conversationId).lastSequence;
        }

        function getFirstSequence(conversationId) {
            const messages = getState(conversationId).messages;
            return messages.length > 0 ? messages[0].sequence : 0;
        }

        function normalizeSummary(summary) {
            const id = normalizeConversationId(summary?.id);
            return {
                ...summary,
                id,
                clientId: Number(summary.clientId),
                clientUserId: summary.clientUserId == null ? null : Number(summary.clientUserId),
                adminUserId: summary.adminUserId == null ? null : Number(summary.adminUserId),
                latestSequence: Number(summary.latestSequence || 0),
                lastReadSequence: Number(summary.lastReadSequence || 0),
                unreadCount: Number(summary.unreadCount || 0),
                version: Number(summary.version || 0)
            };
        }

        function upsertConversation(summary) {
            const normalized = normalizeSummary(summary);
            const existing = conversationSummaries.get(normalized.id);
            const merged = { ...existing, ...normalized };
            conversationSummaries.set(merged.id, merged);
            return merged;
        }

        function replaceConversations(summaries) {
            conversationSummaries.clear();
            for (const summary of summaries || []) upsertConversation(summary);
            return listConversations();
        }

        function getConversation(conversationId) {
            return conversationSummaries.get(normalizeConversationId(conversationId)) || null;
        }

        function listConversations() {
            return Array.from(conversationSummaries.values())
                .sort((left, right) => right.id - left.id);
        }

        function removeConversation(conversationId) {
            const id = normalizeConversationId(conversationId);
            conversationSummaries.delete(id);
            conversations.delete(id);
            clearDraft(id);
            clearPending(id);
        }

        function markConversationRead(conversationId, throughSequence) {
            const summary = getConversation(conversationId);
            if (!summary) return null;
            summary.lastReadSequence = Math.max(
                Number(summary.lastReadSequence || 0),
                Number(throughSequence || 0));
            summary.unreadCount = Math.max(
                Number(summary.latestSequence || 0) - summary.lastReadSequence,
                0);
            return summary;
        }

        function draftKey(conversationId) {
            return `cbssupport:messaging:draft:${draftScope}:${normalizeConversationId(conversationId)}`;
        }

        function saveDraft(conversationId, text) {
            const key = draftKey(conversationId);
            const value = String(text || "");
            volatileDrafts.set(key, value);
            try {
                global.localStorage.setItem(key, value);
            } catch {
                // Storage can be unavailable in private or restricted browser contexts.
            }
        }

        function loadDraft(conversationId) {
            const key = draftKey(conversationId);
            try {
                const stored = global.localStorage.getItem(key);
                if (stored !== null) return stored;
            } catch {
                // Use the in-memory copy when persistent storage is unavailable.
            }
            return volatileDrafts.get(key) || "";
        }

        function clearDraft(conversationId) {
            const key = draftKey(conversationId);
            volatileDrafts.delete(key);
            try {
                global.localStorage.removeItem(key);
            } catch {
                // The draft was still cleared from the in-memory fallback.
            }
        }

        function pendingKey(conversationId) {
            return `cbssupport:messaging:pending:${draftScope}:${normalizeConversationId(conversationId)}`;
        }

        function readPendingCollection(conversationId) {
            const key = pendingKey(conversationId);
            try {
                const stored = global.localStorage.getItem(key);
                if (stored) {
                    const parsed = JSON.parse(stored);
                    return Array.isArray(parsed) ? parsed : [parsed];
                }
            } catch {
                // Ignore malformed/unavailable persistent storage.
            }
            return volatilePending.get(key) || [];
        }

        function writePendingCollection(conversationId, pendingItems) {
            const key = pendingKey(conversationId);
            volatilePending.set(key, pendingItems);
            try {
                global.localStorage.setItem(key, JSON.stringify(pendingItems));
            } catch {
                // The in-memory copy still supports retries in this page.
            }
        }

        function savePending(conversationId, pending) {
            const value = {
                clientMessageId: String(pending.clientMessageId),
                text: String(pending.text || ""),
                attachmentIds: Array.isArray(pending.attachmentIds)
                    ? pending.attachmentIds.map(String)
                    : [],
                state: String(pending.state || "pending")
            };
            const items = readPendingCollection(conversationId);
            const key = value.clientMessageId.toLowerCase();
            const index = items.findIndex(item =>
                String(item.clientMessageId || "").toLowerCase() === key);
            if (index >= 0) items[index] = value;
            else items.push(value);
            writePendingCollection(conversationId, items);
            return value;
        }

        function loadPending(conversationId) {
            const items = readPendingCollection(conversationId);
            return items.length > 0 ? items[items.length - 1] : null;
        }

        function getPending(conversationId) {
            return readPendingCollection(conversationId).slice();
        }

        function findPending(conversationId, predicate) {
            return readPendingCollection(conversationId).find(predicate) || null;
        }

        function clearPending(conversationId, clientMessageId) {
            const key = pendingKey(conversationId);
            if (clientMessageId) {
                const id = String(clientMessageId).toLowerCase();
                const remaining = readPendingCollection(conversationId).filter(item =>
                    String(item.clientMessageId || "").toLowerCase() !== id);
                if (remaining.length > 0) {
                    writePendingCollection(conversationId, remaining);
                    return;
                }
            }
            volatilePending.delete(key);
            try {
                global.localStorage.removeItem(key);
            } catch {
                // The pending operation was still cleared in memory.
            }
        }

        return {
            upsert,
            replace,
            getMessages,
            getLastSequence,
            getFirstSequence,
            upsertConversation,
            replaceConversations,
            getConversation,
            listConversations,
            removeConversation,
            markConversationRead,
            saveDraft,
            loadDraft,
            clearDraft,
            savePending,
            loadPending,
            getPending,
            listPending: getPending,
            findPending,
            clearPending
        };
    }

    namespace.createStore = createStore;
})(window);
