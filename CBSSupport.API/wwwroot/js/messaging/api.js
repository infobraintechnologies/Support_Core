(function (global) {
    "use strict";

    const namespace = global.CBSSupportMessaging = global.CBSSupportMessaging || {};

    function createApi(options = {}) {
        const baseUrl = String(options.baseUrl || "/api/v1/conversations").replace(/\/$/, "");

        async function parseResponse(response) {
            const contentType = response.headers.get("content-type") || "";
            const body = response.status !== 204 && contentType.includes("json")
                ? await response.json()
                : null;

            if (!response.ok) {
                const error = new Error(
                    body?.detail || body?.title || `Messaging request failed (${response.status}).`);
                error.status = response.status;
                error.problem = body;
                throw error;
            }

            return body;
        }

        async function listConversations(query = {}, signal) {
            const parameters = new URLSearchParams();
            parameters.set("limit", String(Math.min(Math.max(Number(query.limit) || 100, 1), 100)));
            if (Number(query.beforeConversationId) > 0) {
                parameters.set("beforeConversationId", String(Number(query.beforeConversationId)));
            }

            const response = await fetch(`${baseUrl}?${parameters}`, {
                credentials: "same-origin",
                headers: { "Accept": "application/json" },
                signal
            });
            const body = await parseResponse(response);
            return {
                items: Array.isArray(body?.items) ? body.items : [],
                nextCursor: body?.nextCursor ?? null
            };
        }

        async function getMessages(conversationId, query = {}, signal) {
            const parameters = new URLSearchParams();
            if (Number(query.afterSequence) > 0) {
                parameters.set("afterSequence", String(Number(query.afterSequence)));
            }
            if (Number(query.beforeSequence) > 0) {
                parameters.set("beforeSequence", String(Number(query.beforeSequence)));
            }
            parameters.set("limit", String(Math.min(Math.max(Number(query.limit) || 100, 1), 100)));

            const response = await fetch(
                `${baseUrl}/${Number(conversationId)}/messages?${parameters}`,
                { credentials: "same-origin", signal });
            const body = await parseResponse(response);

            if (Array.isArray(body)) {
                return { items: body, nextCursor: null };
            }

            return {
                items: Array.isArray(body?.items) ? body.items : [],
                nextCursor: body?.nextCursor ?? null
            };
        }

        async function sendMessage(conversationId, request, signal) {
            const response = await fetch(`${baseUrl}/${Number(conversationId)}/messages`, {
                method: "POST",
                credentials: "same-origin",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({
                    clientMessageId: request.clientMessageId,
                    text: request.text || null,
                    attachmentIds: Array.isArray(request.attachmentIds)
                        ? request.attachmentIds
                        : []
                }),
                signal
            });

            return parseResponse(response);
        }

        async function getAvailableAdmins(signal) {
            const response = await fetch(`${baseUrl}/available-admins`, {
                credentials: "same-origin",
                headers: { "Accept": "application/json" },
                signal
            });
            const body = await parseResponse(response);
            return Array.isArray(body) ? body : [];
        }

        async function getAvailableClientUsers(clientId, signal) {
            const id = Number(clientId);
            const response = await fetch(`/api/v1/admin/clients/${id}/conversation-users`, {
                credentials: "same-origin",
                headers: { "Accept": "application/json" },
                signal
            });
            const body = await parseResponse(response);
            return Array.isArray(body) ? body : [];
        }

        async function getOrCreatePrivate(counterpartyUserId, signal) {
            const response = await fetch(`${baseUrl}/private`, {
                method: "POST",
                credentials: "same-origin",
                headers: {
                    "Accept": "application/json",
                    "Content-Type": "application/json"
                },
                body: JSON.stringify({ counterpartyUserId: Number(counterpartyUserId) }),
                signal
            });
            return parseResponse(response);
        }

        async function advanceRead(conversationId, throughSequence, signal) {
            const response = await fetch(`${baseUrl}/${Number(conversationId)}/read`, {
                method: "PUT",
                credentials: "same-origin",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ throughSequence: Number(throughSequence) }),
                signal
            });
            await parseResponse(response);
        }

        async function transfer(conversationId, request, signal) {
            const response = await fetch(`${baseUrl}/${Number(conversationId)}/assignment`, {
                method: "PUT",
                credentials: "same-origin",
                headers: {
                    "Accept": "application/json",
                    "Content-Type": "application/json"
                },
                body: JSON.stringify({
                    adminUserId: Number(request.adminUserId),
                    expectedVersion: Number(request.expectedVersion),
                    reason: request.reason || null
                }),
                signal
            });
            return parseResponse(response);
        }

        async function archive(conversationId, expectedVersion, signal) {
            const response = await fetch(`${baseUrl}/${Number(conversationId)}/archive`, {
                method: "PUT",
                credentials: "same-origin",
                headers: {
                    "Accept": "application/json",
                    "Content-Type": "application/json"
                },
                body: JSON.stringify({ expectedVersion: Number(expectedVersion) }),
                signal
            });
            return parseResponse(response);
        }

        async function getOrCreateGroup(signal) {
            const response = await fetch(`${baseUrl}/group`, {
                method: "POST",
                credentials: "same-origin",
                signal
            });
            return parseResponse(response);
        }

        async function getOrCreateGroupForTenant(clientId, signal) {
            const id = Number(clientId);
            if (!Number.isSafeInteger(id) || id <= 0) {
                throw new Error("A tenant must be explicitly selected.");
            }
            const response = await fetch(`/api/v1/admin/clients/${id}/group-conversation`, {
                method: "POST",
                credentials: "same-origin",
                signal
            });
            return parseResponse(response);
        }

        return {
            listConversations,
            getMessages,
            sendMessage,
            getOrCreateGroup,
            getOrCreateGroupForTenant,
            getAvailableAdmins,
            getAvailableClientUsers,
            getOrCreatePrivate,
            advanceRead,
            transfer,
            archive
        };
    }

    namespace.createApi = createApi;
})(window);
