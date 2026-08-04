/**
 * Admin Panel SignalR Module
 * Handles all SignalR connection and real-time communication
 */
"use strict";

window.AdminSignalR = (() => {

    // ============================================
    // 🔐 CONNECTION MANAGEMENT
    // ============================================

    let connection = null;
    let messaging = null;

    async function initialize() {
        try {
            const currentUser = window.AdminCore?.getCurrentUser();
            messaging = window.CBSSupportMessaging.createClient({
                hubUrl: "/chathub",
                baseUrl: "/api/v1/conversations",
                draftScope: `admin:${currentUser?.id || "unknown"}`
            });
            connection = messaging.connection;

            setupConnectionEvents();
            await messaging.start();

            console.log("✅ AdminSignalR: Connection established successfully");
            return connection;
        } catch (error) {
            console.error("❌ AdminSignalR: Connection failed:", error);
            throw error;
        }
    }

    function setupConnectionEvents() {
        if (!connection) return;

        // Connection state events
        messaging.on("state", ({ state }) => {
            window.AdminChat?.handleConnectionState(state);
            if (state === "reconnecting") {
                console.log("🔄 AdminSignalR: Attempting to reconnect...");
            }
        });

        messaging.on("sendstate", sendState => {
            if (!sendState?.state) return;
            window.AdminChat?.handleSendState?.(sendState);
        });

        messaging.on("conversationchanged", change => {
            window.AdminChat?.handleConversationChanged(change);
        });

        // Setup message handlers
        setupMessageHandlers();
    }

    // ============================================
    // 📨 MESSAGE HANDLERS
    // ============================================

    function setupMessageHandlers() {
        if (!connection) return;

        // Receive private messages
        messaging.on("message", ({ message: createdMessage, source }) => {
            const message = toLegacyMessage(createdMessage);

            const currentUser = window.AdminCore?.getCurrentUser();
            if (!currentUser) return;

            // Ignore own messages
            if (source === "realtime"
                && (message.insertUser === currentUser.id || message.clientAuthUserId === currentUser.id)) {
                return;
            }

            const conversationId = message.instructionId;
            if (!conversationId) {
                console.error("📨 AdminSignalR: Received message with no instructionId:", message);
                return;
            }

            // Handle floating chat updates
            handleFloatingChatMessage(conversationId, message);

            // Handle main chat updates
            if (window.AdminChat) {
                window.AdminChat.handleIncomingMessage(message);
            }

            // Update conversation list
            updateConversationItem(conversationId, message);

            // Show notification if not on chats page
            if (source !== "reconcile") handleChatPageNotification(message);

            // Load new notifications
            if (window.AdminNotifications) {
                window.AdminNotifications.loadNotifications();
            }
        });

        messaging.on("typing", typing => {
            window.AdminChat?.handleTypingChanged(typing);
        });

        // New ticket notifications
        connection.on("NewTicket", (ticket) => {
            console.log("🎫 AdminSignalR: New ticket received:", ticket);

            const currentClientId = window.AdminCore?.getCurrentClientId();
            if (String(ticket.clientId) === String(currentClientId)) {
                // Refresh dashboard if active
                if ($('#dashboard-page').hasClass('active') && window.AdminDashboard) {
                    window.AdminDashboard.loadEnhancedDashboardData(currentClientId);
                }

                // Refresh tickets table
                if (window.AdminTickets) {
                    const ticketsTable = window.AdminTickets.getTicketsTable();
                    if (ticketsTable) {
                        ticketsTable.ajax.reload(null, false);
                    }
                }
            }

            // Load notifications
            if (window.AdminNotifications) {
                window.AdminNotifications.loadNotifications();
            }
        });

        connection.on("TicketChanged", () => {
            window.AdminTickets?.getTicketsTable()?.ajax.reload(null, false);
            window.AdminNotifications?.loadNotifications();
        });

        connection.on("InquiryChanged", () => {
            window.AdminInquiries?.getInquiriesTable?.()?.ajax.reload(null, false);
            window.AdminNotifications?.loadNotifications();
        });

        // General notifications
        connection.on("ReceiveNotification", (notification) => {
            console.log("🔔 AdminSignalR: Notification received:", notification);

            // Show browser notification if permission granted
            if (Notification.permission === "granted") {
                new Notification(notification.title, {
                    body: notification.message,
                    icon: "/images/notification-icon.png"
                });
            }

            // Load notifications
            if (window.AdminNotifications) {
                window.AdminNotifications.loadNotifications();
            }

            // Show toast notification
            if (window.AdminUtils) {
                window.AdminUtils.showNotification(notification.message, 'info');
            }
        });

        // Ticket status updates
        connection.on("TicketStatusUpdated", (data) => {
            console.log("🎫 AdminSignalR: Ticket status updated:", data);

            if (window.AdminTickets) {
                const ticketsTable = window.AdminTickets.getTicketsTable();
                if (ticketsTable) {
                    ticketsTable.ajax.reload(null, false);
                }
            }

            if (window.AdminUtils) {
                window.AdminUtils.showNotification(`Ticket #${data.ticketId} status updated`, 'info');
            }
        });

        // Inquiry status updates
        connection.on("InquiryStatusUpdated", (data) => {
            console.log("❓ AdminSignalR: Inquiry status updated:", data);

            if (window.AdminInquiries) {
                const inquiriesTable = window.AdminInquiries.getInquiriesTable();
                if (inquiriesTable) {
                    inquiriesTable.ajax.reload(null, false);
                }
            }

            if (window.AdminUtils) {
                window.AdminUtils.showNotification(`Inquiry #${data.inquiryId} status updated`, 'info');
            }
        });
    }

    function toLegacyMessage(message) {
        return {
            id: message.id,
            instructionId: message.conversationId,
            instruction: message.text,
            dateTime: message.sentAt,
            senderName: message.sender?.displayName,
            insertUser: message.sender?.kind === "Admin"
                ? (message.sender.userId ?? message.sender.id)
                : null,
            clientAuthUserId: message.sender?.kind === "Client"
                ? (message.sender.userId ?? message.sender.id)
                : null,
            attachmentId: message.attachmentId,
            clientMessageId: message.clientMessageId,
            sequence: message.sequence,
            attachments: Array.isArray(message.attachments)
                ? message.attachments
                : (Array.isArray(message.safeAttachments) ? message.safeAttachments : [])
        };
    }

    // ============================================
    // 🎯 MESSAGE HANDLING HELPERS
    // ============================================

    function handleFloatingChatMessage(conversationId, message) {
        const floatingChat = document.getElementById(`chatbox-tkt-${conversationId}`) ||
            document.getElementById(`chatbox-inq-${conversationId}`);

        if (floatingChat && !floatingChat.classList.contains('collapsed')) {
            const container = floatingChat.querySelector('.chat-box-body');
            if (message.id && container.querySelector(`[data-message-id="${Number(message.id)}"]`)) return;
            const senderName = message.senderName || 'Client';
            const currentUser = window.AdminCore?.getCurrentUser();
            const isSent = message.insertUser === currentUser?.id;

            const msgRow = document.createElement('div');
            msgRow.className = `message-row ${isSent ? 'sent' : 'received'}`;
            if (message.id) msgRow.dataset.messageId = String(message.id);
            const content = document.createElement('div');
            content.className = 'message-content';
            const bubble = document.createElement('div');
            bubble.className = 'message-bubble';
            const text = document.createElement('p');
            text.className = 'message-text';
            text.textContent = message.instruction || '';
            bubble.appendChild(text);
            window.CBSSupportAttachments?.renderMessageAttachments(
                bubble,
                message.attachments || []);
            const timestamp = document.createElement('span');
            timestamp.className = 'message-timestamp';
            timestamp.textContent = `${senderName} - ${AdminUtils.formatTimestamp(message.dateTime)}`;
            content.append(bubble, timestamp);
            msgRow.appendChild(content);

            container.appendChild(msgRow);
            AdminUtils.scrollToBottom(container);
        }
    }

    function updateConversationItem(conversationId, message) {
        const convItem = document.querySelector(`.conversation-item[data-id="${conversationId}"]`) ||
            document.querySelector(`.admin-conversation-item[data-id="${conversationId}"]`);

        if (convItem) {
            convItem.classList.add('has-unread');
            const subtitle = convItem.querySelector('.text-muted, .admin-conversation-subtitle');
            if (subtitle) {
                subtitle.textContent = message.instruction;
            }
        }
    }

    function handleChatPageNotification(message) {
        if (!$('#chats-page').hasClass('active')) {
            console.log("📨 AdminSignalR: New client message received while not on chats page");

            const chatsNavLink = document.querySelector('[data-page="chats"]');
            if (chatsNavLink && !chatsNavLink.classList.contains('has-notification')) {
                chatsNavLink.classList.add('has-notification');
                const badge = document.createElement('span');
                badge.className = 'badge bg-danger ms-2 notification-badge';
                badge.textContent = '!';
                chatsNavLink.appendChild(badge);
            }

            // Show browser notification
            if (window.Notification && Notification.permission === "granted") {
                new Notification("New Message", {
                    body: `${message.senderName}: ${message.instruction}`,
                    icon: "/images/notification-icon.png"
                });
            }
        }
    }

    // ============================================
    // 📤 SEND FUNCTIONS
    // ============================================

    async function sendMessage(
        conversationId,
        text,
        useMessagingV2 = true,
        attachmentIds = []) {
        try {
            if (!messaging) throw new Error("Messaging is not initialized");
            if (!useMessagingV2) {
                const response = await fetch("/v1/api/instructions/reply", {
                    method: "POST",
                    credentials: "same-origin",
                    headers: { "Content-Type": "application/json" },
                    body: JSON.stringify({
                        instruction: text,
                        instructionId: Number(conversationId)
                    })
                });
                const contentType = response.headers.get("content-type") || "";
                const body = contentType.includes("json")
                    ? await response.json()
                    : null;
                if (!response.ok) {
                    throw new Error(
                        body?.detail
                        || body?.message
                        || `Request failed (${response.status}).`);
                }
                return body;
            }
            const message = await messaging.send(
                Number(conversationId),
                text,
                null,
                attachmentIds);
            return toLegacyMessage(message);
        } catch (error) {
            console.error("❌ AdminSignalR: Error sending admin message:", error);
            throw error;
        }
    }

    async function joinConversation(chatId, useMessagingV2 = true) {
        try {
            const conversationId = Number(chatId);
            if (useMessagingV2) {
                await messaging.join(conversationId);
            } else {
                await messaging.joinRealtime(conversationId);
            }
        } catch (error) {
            console.error("❌ AdminSignalR: Error joining private chat:", error);
            throw error;
        }
    }

    // ============================================
    // 🔗 PUBLIC API
    // ============================================

    return {
        initialize,
        getConnection: () => connection,
        getMessaging: () => messaging,
        sendMessage,
        joinConversation,
        leaveConversation: conversationId => messaging?.leave(Number(conversationId)),
        setTyping: (conversationId, isTyping) =>
            messaging?.setTyping(Number(conversationId), Boolean(isTyping))
    };
})();
