(function (global) {
    "use strict";

    const namespace = global.CBSSupportMessaging = global.CBSSupportMessaging || {};

    function createReconnectPolicy(options) {
        const maximumDelay = Math.max(Number(options.maximumReconnectDelay) || 30000, 1000);
        const baseDelay = Math.max(Number(options.baseReconnectDelay) || 1000, 250);
        return {
            nextRetryDelayInMilliseconds(context) {
                const exponential = Math.min(
                    maximumDelay,
                    baseDelay * Math.pow(2, Math.min(context.previousRetryCount, 5)));
                const jitter = 0.75 + Math.random() * 0.5;
                return Math.round(exponential * jitter);
            }
        };
    }

    function createTransport(options = {}) {
        if (!global.signalR) {
            throw new Error("The pinned SignalR browser client must load before messaging transport.");
        }

        const connection = new signalR.HubConnectionBuilder()
            .withUrl(options.hubUrl || "/chathub")
            .withAutomaticReconnect(createReconnectPolicy(options))
            .build();
        const listeners = new Map();
        const joinedConversations = new Set();
        let startPromise = null;
        let retryTimer = null;
        let startRetryAttempt = 0;

        function emit(name, payload) {
            for (const listener of listeners.get(name) || []) {
                try {
                    listener(payload);
                } catch (error) {
                    console.error(`Messaging ${name} listener failed.`, error);
                }
            }
        }

        function on(name, listener) {
            if (!listeners.has(name)) listeners.set(name, new Set());
            listeners.get(name).add(listener);
            return () => listeners.get(name)?.delete(listener);
        }

        connection.on("MessageCreated", envelope => {
            emit("message", envelope?.data || envelope);
        });
        connection.on("ConversationChanged", envelope => {
            emit("conversationchanged", {
                eventId: envelope?.eventId || null,
                eventType: envelope?.eventType || envelope?.data?.changeType || null,
                occurredAt: envelope?.occurredAt || null,
                conversationId: Number(envelope?.conversationId || 0),
                data: envelope?.data || envelope
            });
        });
        connection.on("TypingChanged", typing => emit("typing", typing));
        connection.onreconnecting(error => emit("state", { state: "reconnecting", error }));
        connection.onclose(error => {
            startPromise = null;
            emit("state", { state: "disconnected", error });
            scheduleStartRetry();
        });
        connection.onreconnected(async connectionId => {
            startRetryAttempt = 0;
            emit("state", { state: "reconnected", connectionId });
            const rejoined = await rejoinAuthorizedConversations();
            emit("rejoined", rejoined);
        });

        async function rejoinAuthorizedConversations() {
            const rejoined = [];
            for (const conversationId of joinedConversations) {
                try {
                    await connection.invoke("JoinConversation", conversationId);
                    rejoined.push(conversationId);
                } catch (error) {
                    joinedConversations.delete(conversationId);
                    emit("error", { operation: "rejoin", conversationId, error });
                }
            }
            return rejoined;
        }

        function scheduleStartRetry() {
            if (retryTimer || connection.state !== signalR.HubConnectionState.Disconnected) return;
            const maximumDelay = Math.max(Number(options.maximumReconnectDelay) || 30000, 1000);
            const exponential = Math.min(maximumDelay, 1000 * Math.pow(2, Math.min(startRetryAttempt, 5)));
            const delay = Math.round(exponential * (0.75 + Math.random() * 0.5));
            startRetryAttempt += 1;
            retryTimer = global.setTimeout(() => {
                retryTimer = null;
                start().catch(() => scheduleStartRetry());
            }, delay);
        }

        async function start() {
            if (connection.state === signalR.HubConnectionState.Connected) return connection;
            if (!startPromise) {
                startPromise = connection.start()
                    .then(async () => {
                        if (retryTimer) {
                            global.clearTimeout(retryTimer);
                            retryTimer = null;
                        }
                        startRetryAttempt = 0;
                        emit("state", { state: "connected" });
                        if (joinedConversations.size > 0) {
                            const rejoined = await rejoinAuthorizedConversations();
                            emit("rejoined", rejoined);
                        }
                        return connection;
                    })
                    .catch(error => {
                        startPromise = null;
                        emit("state", { state: "disconnected", error });
                        scheduleStartRetry();
                        throw error;
                    });
            }
            return startPromise;
        }

        async function join(conversationId) {
            const id = Number(conversationId);
            await start();
            await connection.invoke("JoinConversation", id);
            joinedConversations.add(id);
        }

        async function leave(conversationId) {
            const id = Number(conversationId);
            joinedConversations.delete(id);
            if (connection.state === signalR.HubConnectionState.Connected) {
                await connection.invoke("LeaveConversation", id);
            }
        }

        async function setTyping(conversationId, isTyping) {
            if (connection.state !== signalR.HubConnectionState.Connected) return;
            await connection.invoke("SetTyping", Number(conversationId), Boolean(isTyping));
        }

        global.addEventListener("online", () => {
            start().catch(error => emit("error", { operation: "connect", error }));
        });

        return {
            connection,
            start,
            join,
            leave,
            setTyping,
            on,
            isConnected: () => connection.state === signalR.HubConnectionState.Connected,
            getJoinedConversationIds: () => Array.from(joinedConversations)
        };
    }

    namespace.createTransport = createTransport;
})(window);
