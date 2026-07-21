"use strict";

document.addEventListener("DOMContentLoaded", () => {
    let currentUser = {
        name: serverData.currentUserName,
        id: serverData.currentUserId,
    };

    let currentClient = {
        id: serverData.currentClientId,
        name: serverData.currentUserName
    };

    let currentChatContext = {};
    let lastMessageDate = null;
    let currentTicketData = null;

    let ticketsDataTable = null;
    let inquiriesDataTable = null;

    let clientUnreadNotificationCount = 0;
    let clientNotificationPollingInterval = null;

    const fullscreenBtn = document.getElementById("fullscreen-btn");
    const messageInput = document.getElementById("message-input");
    const sendButton = document.getElementById("send-button");
    const chatPanelBody = document.getElementById("chat-panel-body");
    const chatHeader = document.getElementById("chat-header");
    const fileInput = document.getElementById("file-input");

    const supportTicketsTableE1 = $('#supportTicketsDataTable');
    const inquiriesTableE1 = $('#inquiriesDataTable');

    const connection = new signalR.HubConnectionBuilder()
        .withUrl("/chathub")
        .withAutomaticReconnect()
        .build();

    connection.onreconnected(async () => {
        if (currentChatContext.id) {
            try {
                await connection.invoke("JoinConversation", Number(currentChatContext.id));
            } catch (error) {
                console.error("Failed to rejoin the active conversation:", error);
            }
        }
    });

    const formatTimestamp = (d) => new Date(d).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });

    const toLegacyMessage = (message) => ({
        id: message.id,
        instructionId: message.conversationId,
        instruction: message.text,
        dateTime: message.sentAt,
        senderName: message.sender?.displayName,
        insertUser: message.sender?.kind === "Admin" ? message.sender.userId : null,
        clientAuthUserId: message.sender?.kind === "Client" ? message.sender.userId : null,
        attachmentId: message.attachmentId
    });

    const updateSendButtonState = () => {
        if (!sendButton) return;
        sendButton.disabled = connection.state !== "Connected" || (!messageInput.value.trim() && fileInput.files.length === 0);
    };

    const scrollToBottom = () => {
        chatPanelBody.scrollTop = chatPanelBody.scrollHeight;
    };

    function escapeHtml(text) {
        if (text === null || typeof text === 'undefined') return '';
        const div = document.createElement('div');
        div.textContent = text;
        return div.innerHTML;
    }

    const generatePriorityBadge = (priority) => {
        const p = priority ? priority.toLowerCase() : 'normal';
        const badgeClass = `badge-priority-${p}`;
        const icon = p === 'urgent' ? 'fas fa-exclamation-triangle' :
            p === 'high' ? 'fas fa-exclamation' :
                p === 'normal' || p === 'medium' ? 'fas fa-minus' : 'fas fa-arrow-down';

        return `<span class="badge ${badgeClass}">
        <i class="${icon} me-1" style="font-size: 0.7rem"></i>${escapeHtml(priority || 'Normal')}
    </span>`;
    };

    const formatDateForSeparator = (dStr) => {
        const d = new Date(dStr);
        const today = new Date();
        const yesterday = new Date(today);
        yesterday.setDate(yesterday.getDate() - 1);
        if (d.toDateString() === today.toDateString()) return "Today";
        if (d.toDateString() === yesterday.toDateString()) return "Yesterday";
        return d.toLocaleDateString([], { year: "numeric", month: "long", day: "numeric" });
    };

    function addDateSeparatorIfNeeded(msgDateStr) {
        if (!chatPanelBody) return;
        const dateStr = new Date(msgDateStr).toDateString();
        if (lastMessageDate !== dateStr) {
            lastMessageDate = dateStr;
            const ds = document.createElement("div");
            ds.className = "date-separator";
            ds.textContent = formatDateForSeparator(msgDateStr);
            chatPanelBody.appendChild(ds);
        }
    }
    function getTimeAgo(dateString) {
        const now = new Date();
        const date = new Date(dateString);
        const diffInMs = now - date;
        const diffInMinutes = Math.floor(diffInMs / (1000 * 60));
        const diffInHours = Math.floor(diffInMinutes / 60);
        const diffInDays = Math.floor(diffInHours / 24);

        if (diffInMinutes < 1) return 'Just now';
        if (diffInMinutes < 60) return `${diffInMinutes}m ago`;
        if (diffInHours < 24) return `${diffInHours}h ago`;
        if (diffInDays < 7) return `${diffInDays}d ago`;
        return date.toLocaleDateString();
    }

    function getNotificationIcon(type) {
        const icons = {
            'ticket': 'fas fa-ticket-alt',
            'inquiry': 'fas fa-question-circle',
            'message': 'fas fa-comment',
            'status_change': 'fas fa-exchange-alt'
        };
        return icons[type] || 'fas fa-bell';
    }

    function showNotificationToast(message, type = 'info') {
        let toastContainer = document.getElementById('toast-container');
        if (!toastContainer) {
            toastContainer = document.createElement('div');
            toastContainer.id = 'toast-container';
            toastContainer.className = 'position-fixed top-0 end-0 p-3';
            toastContainer.style.zIndex = '1055';
            document.body.appendChild(toastContainer);
        }

        const toastId = `toast-${Date.now()}`;
        const iconClass = type === 'success' ? 'fa-check-circle text-success' :
            type === 'error' ? 'fa-exclamation-triangle text-danger' :
                'fa-info-circle text-info';

        const toastHtml = `
        <div id="${toastId}" class="toast" role="alert" aria-live="assertive" aria-atomic="true">
            <div class="toast-header">
                <i class="fas ${iconClass} me-2"></i>
                <strong class="me-auto">Notification</strong>
                <button type="button" class="btn-close" data-bs-dismiss="toast" aria-label="Close"></button>
            </div>
            <div class="toast-body">
                ${escapeHtml(message)}
            </div>
        </div>`;

        toastContainer.insertAdjacentHTML('beforeend', toastHtml);

        const toastElement = document.getElementById(toastId);
        const toast = new bootstrap.Toast(toastElement, {
            autohide: true,
            delay: 5000
        });

        toast.show();

        toastElement.addEventListener('hidden.bs.toast', () => {
            toastElement.remove();
        });
    }

    function populateTicketDetailsModal(ticketData) {
        console.log('Populating modal with:', ticketData);

        $('#details-id').text(`#${ticketData.id || 'N/A'}`);
        $('#details-subject').text(ticketData.subject || 'N/A');

        if (ticketData.date) {
            const date = new Date(ticketData.date);
            $('#details-date').text(date.toLocaleString());
        } else {
            $('#details-date').text('N/A');
        }

        $('#details-createdBy').text(ticketData.createdBy || 'N/A');
        $('#details-resolvedBy').text(ticketData.resolvedBy || 'N/A');

        const status = ticketData.status || 'Pending';
        const statusClass = `badge-status-${status.toLowerCase()}`;
        $('#details-status').html(`<span class="badge ${statusClass}">${escapeHtml(status)}</span>`);

        $('#details-priority').html(generatePriorityBadge(ticketData.priority));
        $('#details-description').text(ticketData.instruction || ticketData.description || 'No description provided.');

        let remarksText = 'N/A';
        try {
            if (ticketData.remarks) {
                const remarksObj = JSON.parse(ticketData.remarks);
                remarksText = remarksObj.userremarks || remarksObj.remarks || ticketData.remarks;
            }
        } catch (e) {
            remarksText = ticketData.remarks || 'N/A';
        }
        $('#details-remarks').text(remarksText);

        if (ticketData.expiryDate) {
            const expiryDate = new Date(ticketData.expiryDate);
            $('#details-expiryDate').text(expiryDate.toLocaleString());
        } else {
            $('#details-expiryDate').text('N/A');
        }
    }

    function populateInquiryDetailsModal(inquiryData) {
        console.log('Populating inquiry modal with:', inquiryData);

        $('#inquiry-details-id').text(`#INQ-${inquiryData.id || 'N/A'}`);
        $('#inquiry-details-topic').text(inquiryData.topic || 'N/A');

        if (inquiryData.date) {
            const date = new Date(inquiryData.date);
            $('#inquiry-details-date').text(date.toLocaleString());
        } else {
            $('#inquiry-details-date').text('N/A');
        }

        $('#inquiry-details-inquiredBy').text(inquiryData.inquiredBy || 'N/A');

        const outcome = inquiryData.outcome || 'Pending';
        const mappedOutcome = outcome === 'Completed' ? 'Resolved' : outcome;
        const outcomeClass = `badge-status-${mappedOutcome.toLowerCase()}`;
        $('#inquiry-details-outcome').html(`<span class="badge ${outcomeClass}">${escapeHtml(outcome)}</span>`);

        $('#inquiry-details-description').text(inquiryData.description || inquiryData.instruction || 'No description provided.');
    }

    function closeTicketModal() {
        const modalElement = document.getElementById('viewTicketDetailsModal');
        const modalInstance = bootstrap.Modal.getInstance(modalElement);

        if (modalInstance) {
            modalInstance.hide();
        }

        setTimeout(() => {
            const backdrops = document.querySelectorAll('.modal-backdrop');
            backdrops.forEach(backdrop => backdrop.remove());
            document.body.classList.remove('modal-open');
            document.body.style.overflow = '';
            document.body.style.paddingRight = '';
        }, 150);
    }

    if (fullscreenBtn) {
        const fullscreenIcon = fullscreenBtn.querySelector("i");
        fullscreenBtn.addEventListener("click", () => {
            if (!document.fullscreenElement) {
                document.documentElement.requestFullscreen().catch(err => console.error(err.message));
            } else if (document.exitFullscreen) {
                document.exitFullscreen();
            }
        });
        document.addEventListener("fullscreenchange", () => {
            if (document.fullscreenElement) {
                fullscreenIcon.classList.remove("fa-expand");
                fullscreenIcon.classList.add("fa-compress");
            } else {
                fullscreenIcon.classList.remove("fa-compress");
                fullscreenIcon.classList.add("fa-expand");
            }
        });
    }

    function displayMessage(msg, isHistory = false) {
        if (!chatPanelBody) {
            console.error("CRITICAL: displayMessage was called but 'chatPanelBody' is null!");
            return;
        }

        if (!msg || !msg.dateTime) {
            console.error("Invalid message object received:", msg);
            return;
        }

        addDateSeparatorIfNeeded(msg.dateTime);

        const isSent = msg.clientAuthUserId != null && msg.clientAuthUserId === currentUser.id;
        const senderName = msg.senderName || "Support";

        const row = document.createElement('div');
        row.className = `message-row ${isSent ? 'sent' : 'received'}`;

        row.innerHTML = `
        <div class="message-bubble">
            <div class="message-sender">${escapeHtml(senderName)}</div>
            <p class="message-text">${escapeHtml(msg.instruction || '')}</p>
            <div class="message-timestamp">${formatTimestamp(msg.dateTime)}</div>
        </div>`;

        chatPanelBody.appendChild(row);

        if (!isHistory) {
            scrollToBottom();
        }
    }

    function renderSidebar(sidebarData) {
        const containers = {
            private: document.getElementById('private-chat-list'),
            internal: document.getElementById('internal-chat-list'),
        };
        Object.values(containers).forEach(container => { if (container) container.innerHTML = ''; });

        sidebarData.privateChats?.forEach(item => containers.private?.appendChild(createConversationItem(item, 'private')));
        sidebarData.internalChats?.forEach(item => containers.internal?.appendChild(createConversationItem(item, 'internal')));
    }

    function createConversationItem(itemData, type) {
        const item = document.createElement('a');
        item.href = '#';
        item.className = 'list-group-item list-group-item-action conversation-item';
        item.dataset.id = itemData.conversationId;
        item.dataset.name = itemData.displayName;
        item.dataset.type = type;
        item.dataset.route = itemData.route;

        item.innerHTML = `
            <div class="d-flex w-100 align-items-center">
                <div class="avatar-initials ${itemData.avatarClass || 'avatar-bg-secondary'} me-3">${itemData.avatarInitials || '?'}</div>
                <div class="flex-grow-1">
                    <div class="fw-bold">${escapeHtml(itemData.displayName)}</div>
                    <small class="text-muted">${escapeHtml(itemData.subtitle)}</small>
                </div>
            </div>`;

        return item;
    }

    async function loadSidebarForClient(clientId) {
        try {
            const response = await fetch(`/v1/api/instructions/sidebar/${clientId}`);
            if (!response.ok) throw new Error(`Failed to load sidebar: ${response.statusText}`);
            const sidebarData = await response.json();
            renderSidebar(sidebarData);
        } catch (error) {
            console.error("Error loading sidebar:", error);
        }
    }

    async function switchChatContext(contextData) {
        currentChatContext = {
            id: contextData.id,
            name: contextData.name,
            type: contextData.type,
            route: contextData.route
        };

        console.log("CLIENT: Switched chat context to:", currentChatContext);

        document.querySelectorAll(".conversation-item.active").forEach(el => el.classList.remove("active"));
        const activeItem = document.querySelector(`.conversation-item[data-id="${currentChatContext.id}"]`);
        if (activeItem) activeItem.classList.add("active");

        chatHeader.innerHTML = `<div><div class="fw-bold">${escapeHtml(currentChatContext.name)}</div></div>`;
        if (connection.state !== signalR.HubConnectionState.Connected) {
            console.warn("SignalR connection not ready, waiting...");
            await connection.start();
        }

        try {
            await connection.invoke("JoinConversation", Number(currentChatContext.id));
            await loadMessagesForConversation(currentChatContext.id);
        } catch (error) {
            console.error("Failed to join private chat:", error);
            alert("Failed to join chat. Please try again.");
        }
    }

    async function loadMessagesForConversation(conversationId) {
        if (!chatPanelBody) return;
        chatPanelBody.innerHTML = '<div class="text-center p-3"><div class="spinner-border" role="status"></div></div>';
        lastMessageDate = null;
        try {
            const response = await fetch(`/v1/api/instructions/messages/${conversationId}`);
            if (!response.ok) throw new Error(`Failed to load messages: ${response.statusText}`);
            const messages = await response.json();
            chatPanelBody.innerHTML = '';
            messages.forEach(msg => displayMessage(msg, true));
            scrollToBottom();
        } catch (error) {
            console.error("Error loading messages:", error);
            chatPanelBody.innerHTML = `<p class="text-danger p-3">Error loading messages.</p>`;
        }
    }

    async function sendMessage() {
        if (!messageInput || !currentChatContext.route) return;
        const messageText = messageInput.value.trim();
        if (!messageText) {
            alert("Please enter a message.");
            return;
        }

        try {
            const savedMessage = await connection.invoke(
                "SendMessage",
                Number(currentChatContext.id),
                { text: messageText, attachmentIds: [] });

            displayMessage(toLegacyMessage(savedMessage), false);

            messageInput.value = '';
            updateSendButtonState();
        } catch (error) {
            console.error("Error sending message:", error);
            alert(`Error: ${error.message}`);
        }
    }

    async function loadClientNotifications() {
        try {
            const response = await fetch(`/v1/api/instructions/notifications/unread?clientId=${currentClient.id}`);
            if (!response.ok) throw new Error('Failed to load notifications');

            const unreadInstructions = await response.json();
            const cachedReadNotifications = JSON.parse(localStorage.getItem('client_read_notifications') || '[]');
            const allNotifications = processClientNotifications(unreadInstructions, cachedReadNotifications);

            updateClientNotificationBadge(allNotifications.filter(n => !n.isRead).length);
            renderClientNotifications(allNotifications);

            return allNotifications;
        } catch (error) {
            console.error('Error loading client notifications:', error);
            return [];
        }
    }

    function processClientNotifications(unreadInstructions, cachedReadNotifications = []) {
        const notifications = [];

        unreadInstructions.forEach(instruction => {
            let notification = {
                id: instruction.id,
                title: '',
                message: '',
                type: '',
                entityId: instruction.id,
                entityType: '',
                createdAt: instruction.insert_date || instruction.datetime,
                isRead: instruction.notification_seen_by_client === 1,
                triggerUserName: instruction.sendername || 'Support'
            };

            if (instruction.inst_category_id === 101) {
                notification.type = 'ticket';
                notification.entityType = 'ticket';
                notification.title = 'Ticket Update';
                notification.message = `Your ticket #${instruction.id} has been updated: ${instruction.instruction?.substring(0, 50) || 'Status changed'}...`;
            } else if (instruction.inst_category_id === 102) {
                notification.type = 'inquiry';
                notification.entityType = 'inquiry';
                notification.title = 'Inquiry Response';
                notification.message = `Response to inquiry #${instruction.id}: ${instruction.instruction?.substring(0, 50) || 'New response'}...`;
            } else if (instruction.inst_category_id === 100) {
                notification.type = 'message';
                notification.entityType = 'message';
                notification.title = 'New Message';
                notification.message = `${instruction.sendername || 'Support'}: ${instruction.instruction?.substring(0, 50) || 'New message'}...`;
                notification.entityId = instruction.instruction_id || instruction.id;
            }

            notification.timeAgo = getTimeAgo(notification.createdAt);
            notification.icon = getNotificationIcon(notification.type);
            notifications.push(notification);
        });

        cachedReadNotifications.forEach(cachedNotification => {
            if (!notifications.find(n => n.id === cachedNotification.id)) {
                cachedNotification.isRead = true;
                cachedNotification.timeAgo = getTimeAgo(cachedNotification.createdAt);
                notifications.push(cachedNotification);
            }
        });

        notifications.sort((a, b) => new Date(b.createdAt) - new Date(a.createdAt));
        return notifications.slice(0, 20);
    }

    function updateClientNotificationBadge(count) {
        const badge = document.getElementById('client-notification-count');

        if (badge) {
            if (count > 0) {
                badge.textContent = count > 99 ? '99+' : count.toString();
                badge.style.display = 'block';

                if (count > clientUnreadNotificationCount) {
                    const btn = badge.closest('.client-notification-btn');
                    if (btn) {
                        btn.classList.add('client-notification-shake');
                        setTimeout(() => btn.classList.remove('client-notification-shake'), 500);
                    }
                }
            } else {
                badge.style.display = 'none';
            }
        }

        clientUnreadNotificationCount = count;
    }

    function renderClientNotifications(notifications) {
        const container = document.getElementById('client-notification-list');

        if (!container) return;

        if (!notifications || notifications.length === 0) {
            container.innerHTML = `
                <div class="notification-empty">
                    <i class="fas fa-bell-slash fa-2x mb-2"></i>
                    <p>No notifications yet</p>
                </div>
            `;
            return;
        }

        container.innerHTML = notifications.map(notification => `
            <div class="notification-item ${!notification.isRead ? 'unread' : ''}" 
                 data-id="${notification.id}" 
                 data-entity-id="${notification.entityId}" 
                 data-entity-type="${notification.entityType}">
                <div class="notification-content">
                    <div class="notification-icon ${notification.type}">
                        <i class="${notification.icon}"></i>
                    </div>
                    <div class="notification-text">
                        <div class="notification-title">${escapeHtml(notification.title)}</div>
                        <div class="notification-message">${escapeHtml(notification.message)}</div>
                        <div class="notification-time">${notification.timeAgo}</div>
                    </div>
                </div>
            </div>
        `).join('');
    }

    async function markClientNotificationAsRead(instructionId) {
        try {
            const response = await fetch(`/v1/api/instructions/${instructionId}/mark-seen-client`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' }
            });

            if (response.ok) {
                const notificationElement = document.querySelector(`[data-id="${instructionId}"]`);
                if (notificationElement && notificationElement.classList.contains('unread')) {
                    notificationElement.classList.remove('unread');

                    const notificationData = {
                        id: parseInt(instructionId),
                        entityId: notificationElement.dataset.entityId,
                        entityType: notificationElement.dataset.entityType,
                        title: notificationElement.querySelector('.notification-title').textContent,
                        message: notificationElement.querySelector('.notification-message').textContent,
                        timeAgo: notificationElement.querySelector('.notification-time').textContent,
                        type: notificationElement.querySelector('.notification-icon').className.split(' ').find(c => ['ticket', 'inquiry', 'message', 'status_change'].includes(c)) || 'message',
                        icon: notificationElement.querySelector('.notification-icon i').className,
                        createdAt: new Date().toISOString(),
                        isRead: true
                    };

                    const existingReadNotifications = JSON.parse(localStorage.getItem('client_read_notifications') || '[]');
                    const updatedReadNotifications = [notificationData, ...existingReadNotifications].slice(0, 20);
                    localStorage.setItem('client_read_notifications', JSON.stringify(updatedReadNotifications));
                }

                if (clientUnreadNotificationCount > 0) {
                    updateClientNotificationBadge(clientUnreadNotificationCount - 1);
                }
            }
        } catch (error) {
            console.error('Error marking notification as read:', error);
        }
    }

    async function markAllClientNotificationsAsRead() {
        try {
            const response = await fetch('/v1/api/instructions/mark-all-seen-client', {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' }
            });

            if (response.ok) {
                const currentNotifications = Array.from(document.querySelectorAll('.notification-item')).map(item => {
                    return {
                        id: parseInt(item.dataset.id),
                        entityId: item.dataset.entityId,
                        entityType: item.dataset.entityType,
                        title: item.querySelector('.notification-title').textContent,
                        message: item.querySelector('.notification-message').textContent,
                        timeAgo: item.querySelector('.notification-time').textContent,
                        type: item.querySelector('.notification-icon').className.split(' ').find(c => ['ticket', 'inquiry', 'message', 'status_change'].includes(c)) || 'message',
                        icon: item.querySelector('.notification-icon i').className,
                        createdAt: new Date().toISOString(),
                        isRead: true
                    };
                });

                const existingReadNotifications = JSON.parse(localStorage.getItem('client_read_notifications') || '[]');
                const updatedReadNotifications = [...currentNotifications, ...existingReadNotifications].slice(0, 20);

                localStorage.setItem('client_read_notifications', JSON.stringify(updatedReadNotifications));

                document.querySelectorAll('.notification-item.unread').forEach(item => {
                    item.classList.remove('unread');
                });

                updateClientNotificationBadge(0);
                alert('All notifications marked as read');
            }
        } catch (error) {
            console.error('Error marking all notifications as read:', error);
            alert('Failed to mark notifications as read');
        }
    }

    function initializeClientNotifications() {
        console.log('🔔 Initializing client notifications...');

        const notificationBtn = document.getElementById('client-notification-btn');
        const notificationMenu = document.getElementById('client-notification-menu');

        if (notificationBtn && notificationMenu) {
            notificationBtn.addEventListener('click', async (e) => {
                e.stopPropagation();

                const isVisible = notificationMenu.classList.contains('show');

                if (isVisible) {
                    notificationMenu.classList.remove('show');
                    return;
                }

                try {
                    await loadClientNotifications();
                    notificationMenu.classList.add('show');
                } catch (error) {
                    console.error('❌ Error loading notifications:', error);
                }
            });

            document.addEventListener('click', (e) => {
                if (!e.target.closest('.client-notification-container')) {
                    notificationMenu.classList.remove('show');
                }
            });

            notificationMenu.addEventListener('click', async (e) => {
                const notificationItem = e.target.closest('.notification-item');
                if (notificationItem) {
                    const notificationId = notificationItem.dataset.id;
                    const entityId = notificationItem.dataset.entityId;
                    const entityType = notificationItem.dataset.entityType;

                    if (notificationItem.classList.contains('unread')) {
                        await markClientNotificationAsRead(notificationId);
                    }

                    if (entityType === 'message') {
                        await switchChatContext({
                            id: entityId,
                            name: 'Support Chat',
                            type: 'support',
                            route: 'support-group'
                        });
                    } else if (entityType === 'ticket' && entityId) {
                        if (ticketsDataTable) {
                            ticketsDataTable.ajax.reload();
                        }
                    } else if (entityType === 'inquiry' && entityId) {
                        if (inquiriesDataTable) {
                            inquiriesDataTable.ajax.reload();
                        }
                    }

                    notificationMenu.classList.remove('show');
                }
            });

            const markAllReadBtn = document.getElementById('client-mark-all-read-btn');
            if (markAllReadBtn) {
                markAllReadBtn.addEventListener('click', markAllClientNotificationsAsRead);
            }

            console.log('✅ Client notifications initialized');
        }

        loadClientNotifications();

        if (clientNotificationPollingInterval) {
            clearInterval(clientNotificationPollingInterval);
        }
        clientNotificationPollingInterval = setInterval(loadClientNotifications, 30000);
    }

    function initializeTicketSystem() {
        const newTicketBtn = document.getElementById("newSupportTicketBtn");
        const newInquiryBtn = document.getElementById("newInquiryBtn");

        const createTicketModalEl = document.getElementById("newSupportTicketModal");
        const createInquiryModalEl = document.getElementById("newInquiryModal");

        const createTicketModal = createTicketModalEl ? new bootstrap.Modal(createTicketModalEl) : null;
        const createInquiryModal = createInquiryModalEl ? new bootstrap.Modal(createInquiryModalEl) : null;

        const createTicketForm = document.getElementById("supportTicketForm");
        const createInquiryForm = document.getElementById("inquiryForm");

        if (newTicketBtn) {
            newTicketBtn.addEventListener("click", () => {
                if (createTicketModal) createTicketModal.show();
            });
        }

        if (createTicketForm) {
            createTicketForm.addEventListener("submit", async (e) => {
                e.preventDefault();

                const subjectSelect = document.getElementById("ticketSubject");
                const descriptionInput = document.getElementById("ticketDescription")
                const remarksInput = document.getElementById("ticketRemarks")
                const expiryDateInput = document.getElementById("ticketExpiryDate")

                if (!subjectSelect) {
                    console.error("Could not find element with ID 'ticketSubject'.");
                    alert("An error occured. Could not find the subject field");
                    return;
                }

                const ticketTypeRoute = subjectSelect.value;
                const description = descriptionInput.value;
                const remarks = remarksInput.value;
                const expiryDate = expiryDateInput.value;
                const priority = document.getElementById("ticketPriority").value;

                if (!ticketTypeRoute || !description) {
                    alert("Please fill all the required fields for the ticket.");
                    return;
                }

                const chatMessage = {
                    Instruction: description,
                    Remarks: remarks,
                    Priority: priority,
                    expiryDate: expiryDate,
                    InstructionId: null,
                    ClientId: currentClient.id,
                    ClientAuthUserId: currentUser.id,
                    InsertUser: currentUser.id,
                    InstCategoryId: 101,
                    ServiceId: 3,
                };

                try {
                    const response = await fetch(`/v1/api/instructions/${ticketTypeRoute}`, {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify(chatMessage)
                    });

                    if (!response.ok) {
                        const errorData = await response.json();
                        throw new Error(errorData.message || "Failed to create ticket.");
                    }

                    const createdTicket = await response.json();

                    if (ticketsDataTable) {
                        await new Promise(resolve => {
                            ticketsDataTable.ajax.reload(() => resolve(), false);
                        });
                    }

                    await loadSidebarForClient(currentClient.id);

                    showNotificationToast(`Ticket #${createdTicket.id} created successfully!`, 'success');

                    if (createTicketModal) createTicketModal.hide();
                    createTicketForm.reset();

                } catch (error) {
                    console.error("Error creating ticket:", error);
                    showNotificationToast(`Error: ${error.message}`, 'error');
                }
            });
        }

        if (newInquiryBtn) {
            newInquiryBtn.addEventListener("click", () => {
                if (createInquiryModal) createInquiryModal.show();
            });
        }

        if (createInquiryForm) {
            createInquiryForm.addEventListener("submit", async (e) => {
                e.preventDefault();

                const subjectSelect = document.getElementById("inquirySubject");
                const messageInput = document.getElementById("inquiryMessage");

                if (!subjectSelect) {
                    console.error("Could not find element with ID 'inquirySubject'.");
                    alert("An error occurred. Could not find the subject field.");
                    return;
                }

                const inquiryType = subjectSelect.value;
                const message = messageInput.value;

                let inquiryRoute;

                if (inquiryType === "Account Inquiry") {
                    inquiryRoute = "inquiry/accounts";
                } else if (inquiryType === "Sales and Management") {
                    inquiryRoute = "inquiry/sales";
                } else {
                    alert("Please select a valid inquiry type.");
                    return;
                }

                const chatMessage = {
                    Instruction: message,
                    InstructionId: null,
                    ClientId: currentClient.id,
                    ClientAuthUserId: currentUser.id,
                    InsertUser: currentUser.id,
                    InstCategoryId: 102,
                    ServiceId: 3,
                };

                try {
                    const response = await fetch(`/v1/api/instructions/${inquiryRoute}`, {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify(chatMessage)
                    });

                    if (!response.ok) {
                        const errorData = await response.json();
                        throw new Error(errorData.message || `Failed to create inquiry: ${response.statusText}`);
                    }

                    const createdInquiry = await response.json();

                    if (inquiriesDataTable) {
                        await new Promise(resolve => {
                            inquiriesDataTable.ajax.reload(() => resolve(), false);
                        });
                    }

                    await loadSidebarForClient(currentClient.id);

                    showNotificationToast(`Inquiry #${createdInquiry.id} created successfully!`, 'success');

                    if (createInquiryModal) createInquiryModal.hide();
                    createInquiryForm.reset();

                } catch (error) {
                    console.error("Error creating inquiry:", error);
                    showNotificationToast(`Error: ${error.message}`, 'error');
                }
            });
        }
    }

    connection.on("MessageCreated", (createdMessage) => {
        const message = toLegacyMessage(createdMessage);
        console.log("CLIENT SIDE: 'ReceivePrivateMessage' event fired. Message received:", message);

        const conversationId = message.instructionId;

        console.log(`CLIENT RECEIVER: Comparing incoming message ID (${conversationId}) with currently open chat ID (${currentChatContext.id})`);

        if (message.clientAuthUserId === currentUser.id) {
            console.log("CLIENT RECEIVER: Ignoring own message broadcast to prevent duplicate.");
            return;
        }

        if (currentChatContext && String(currentChatContext.id) === String(conversationId)) {
            displayMessage(message, false);
        }
        else {
            const convItem = document.querySelector(`.conversation-item[data-id="${conversationId}"]`);
            if (convItem) {
                convItem.classList.add('has-unread');
                const subtitle = convItem.querySelector('.text-muted');
                if (subtitle) {
                    subtitle.textContent = message.instruction;
                }
            }
        }

        loadClientNotifications();
    });

    connection.on("TicketStatusUpdated", (data) => {
        console.log("CLIENT: Ticket status updated:", data);

        if (ticketsDataTable) {
            ticketsDataTable.ajax.reload(null, false);
        }

        showNotificationToast(`Ticket #${data.ticketId} status updated to: ${data.newStatus}`, 'info');

        loadClientNotifications();
    });

    connection.on("InquiryStatusUpdated", (data) => {
        console.log("CLIENT: Inquiry status updated:", data);

        if (inquiriesDataTable) {
            inquiriesDataTable.ajax.reload(null, false); 
        }

        showNotificationToast(`Inquiry #${data.inquiryId} status updated to: ${data.newStatus}`, 'info');

        loadClientNotifications();
    });

    connection.on("NewTicketCreated", (data) => {
        console.log("CLIENT: New ticket created:", data);

        if (ticketsDataTable) {
            ticketsDataTable.ajax.reload(null, false);
        }

        loadSidebarForClient(currentClient.id);
    });

    connection.on("NewInquiryCreated", (data) => {
        console.log("CLIENT: New inquiry created:", data);

        if (inquiriesDataTable) {
            inquiriesDataTable.ajax.reload(null, false);
        }

        loadSidebarForClient(currentClient.id);
    });

    async function init() {
        try {
            await connection.start();
            console.log("SignalR Connected.");
            updateSendButtonState();
            await loadSidebarForClient(currentClient.id);

            initializeClientNotifications();

        } catch (err) {
            console.error("Initialization Error: ", err);
            if (chatHeader) chatHeader.innerHTML = `<div class="text-danger">Connection Failed</div>`;
            return;
        }

        if (supportTicketsTableE1.length) {
            ticketsDataTable = supportTicketsTableE1.DataTable({
                "ajax": {
                    "url": "/v1/api/instructions/tickets",
                    "dataSrc": function (json) {
                        console.log("DEBUG: Ticket data structure:", json.data[0]);
                        return json.data;
                    }
                },
                "columns": [
                    {
                        "data": "id",
                        "title": '<i class="fas fa-hashtag me-1"></i>ID',
                        "width": "8%",
                        "className": "text-center fw-bold",
                        "render": function (data) {
                            return `<span class="badge bg-light text-dark border">#${data}</span>`;
                        }
                    },
                    {
                        "data": "subject",
                        "title": '<i class="fas fa-ticket-alt me-1"></i>Subject',
                        "width": "30%",
                        "className": "fw-semibold",
                        "render": function (data, type, row) {
                            const subject = data || 'General Support';
                            const truncated = subject.length > 40 ? subject.substring(0, 40) + '...' : subject;
                            return `<span title="${escapeHtml(subject)}" class="text-primary">${escapeHtml(truncated)}</span>`;
                        }
                    },
                    {
                        "data": "date",
                        "title": '<i class="fas fa-calendar me-1"></i>Date',
                        "width": "15%",
                        "className": "text-center",
                        "render": function (data) {
                            const date = new Date(data);
                            const formatted = date.toLocaleDateString('en-US', {
                                month: 'short',
                                day: 'numeric',
                                year: 'numeric'
                            });
                            const time = date.toLocaleTimeString('en-US', {
                                hour: '2-digit',
                                minute: '2-digit'
                            });
                            return `<div class="text-muted small">${formatted}</div><div class="text-secondary" style="font-size: 0.75rem">${time}</div>`;
                        }

                    },
                    {
                        "data": "status",
                        "title": '<i class="fas fa-info-circle me-1"></i>Status',
                        "width": "12%",
                        "className": "text-center",
                        "render": function (data) {
                            const status = data || 'Pending';
                            const statusClass = `badge-status-${status.toLowerCase()}`;
                            return `<span class="badge ${statusClass}"><i class="fas fa-circle me-1" style="font-size: 0.5rem"></i>${escapeHtml(status)}</span>`;
                        }
                    },
                    {
                        "data": "priority",
                        "title": '<i class="fas fa-exclamation-triangle me-1"></i>Priority',
                        "width": "12%",
                        "className": "text-center",
                        "render": (data) => generatePriorityBadge(data)
                    },
                    {
                        "data": null,
                        "title": '<i class="fas fa-cogs me-1"></i>Actions',
                        "orderable": false,
                        "width": "15%",
                        "className": "text-center",
                        "render": function (data, type, row) {
                            return `
                        <div class="action-buttons">
                            <button class="btn-icon-action view-details-btn" title="View Details" data-bs-toggle="tooltip">
                                <i class="fas fa-eye"></i>
                            </button>
                            <button class="btn-icon-action start-chat-btn" title="Open Chat" data-bs-toggle="tooltip">
                                <i class="fas fa-comments"></i>
                            </button>
                        </div>`;
                        }
                    }
                ],
                "order": [[0, 'desc']],
                "pageLength": 10,
                "lengthMenu": [[5, 10, 25, 50], [5, 10, 25, 50]],
                "language": {
                    "emptyTable": '<div class="text-center p-4"><i class="fas fa-ticket-alt fa-3x text-muted mb-3"></i><br><span class="text-muted">You haven\'t created any support tickets yet.</span><br><small class="text-secondary">Click "New Support Ticket" to get started!</small></div>',
                    "search": '<i class="fas fa-search me-2"></i>',
                    "lengthMenu": 'Show _MENU_ tickets',
                    "info": 'Showing _START_ to _END_ of _TOTAL_ tickets',
                    "infoEmpty": 'No tickets available',
                    "paginate": {
                        "first": '<i class="fas fa-angle-double-left"></i>',
                        "last": '<i class="fas fa-angle-double-right"></i>',
                        "next": '<i class="fas fa-angle-right"></i>',
                        "previous": '<i class="fas fa-angle-left"></i>'
                    }
                },
                "dom": '<"row"<"col-sm-12 col-md-6"l><"col-sm-12 col-md-6"f>>rt<"row"<"col-sm-12 col-md-5"i><"col-sm-12 col-md-7"p>>',
                "responsive": true,
                "processing": true,
                "deferRender": true,
                "stateSave": true,
                "drawCallback": function () {
                    const tooltipTriggerList = [].slice.call(document.querySelectorAll('[data-bs-toggle="tooltip"]'));
                    tooltipTriggerList.map(function (tooltipTriggerEl) {
                        return new bootstrap.Tooltip(tooltipTriggerEl, {
                            trigger: 'hover',
                            delay: { show: 300, hide: 100 }
                        });
                    });
                }
            });

            const searchBox = supportTicketsTableE1.closest('.dataTables_wrapper').find('.dataTables_filter input');
            searchBox.attr('placeholder', 'Search tickets...').addClass('form-control-sm');
        }

        if (inquiriesTableE1.length) {
            inquiriesDataTable = inquiriesTableE1.DataTable({
                "ajax": {
                    "url": "/v1/api/instructions/inquiries",
                    "dataSrc": function (json) {
                        console.log("DEBUG: Inquiry data structure:", json.data[0]);
                        console.log("DEBUG: All inquiry fields:", json.data.length > 0 ? Object.keys(json.data[0]) : "No data");
                        return json.data;
                    }
                },
                "columns": [
                    {
                        "data": "id",
                        "title": '<i class="fas fa-hashtag me-1"></i>ID',
                        "width": "8%",
                        "className": "text-center fw-bold",
                        "render": function (data) {
                            return `<span class="badge bg-light text-dark border">#INQ-${data}</span>`;
                        }
                    },
                    {
                        "data": "topic",
                        "title": '<i class="fas fa-question-circle me-1"></i>Topic',
                        "width": "25%",
                        "className": "fw-semibold text-primary"
                    },
                    {
                        "data": "inquiredBy",
                        "title": '<i class="fas fa-user me-1"></i>Inquired By',
                        "width": "20%"
                    },
                    {
                        "data": "date",
                        "title": '<i class="fas fa-calendar me-1"></i>Date',
                        "width": "15%",
                        "className": "text-center",
                        "render": function (data) {
                            const date = new Date(data);
                            const formatted = date.toLocaleDateString('en-US', {
                                month: 'short',
                                day: 'numeric',
                                year: 'numeric'
                            });
                            const time = date.toLocaleTimeString('en-US', {
                                hour: '2-digit',
                                minute: '2-digit'
                            });
                            return `<div class="text-muted small">${formatted}</div><div class="text-secondary" style="font-size: 0.75rem">${time}</div>`;
                        }
                    },
                    {
                        "data": "outcome",
                        "title": '<i class="fas fa-info-circle me-1"></i>Outcome',
                        "width": "12%",
                        "className": "text-center",
                        "render": function (data, type, row) {
                            const outcome = data || row.outcome || 'Pending';
                            const mappedOutcome = outcome === 'Completed' ? 'Resolved' : outcome;
                            const outcomeClass = `badge-status-${mappedOutcome.toLowerCase()}`;
                            return `<span class="badge ${outcomeClass}"><i class="fas fa-circle me-1" style="font-size: 0.5rem"></i>${escapeHtml(outcome)}</span>`;
                        }
                    },
                    {
                        "data": null,
                        "title": '<i class="fas fa-cogs me-1"></i>Actions',
                        "orderable": false,
                        "width": "20%",
                        "className": "text-center",
                        "render": function (data, type, row) {
                            return `
                    <div class="action-buttons">
                        <button class="btn-icon-action view-details-btn" title="View Details" data-bs-toggle="tooltip">
                            <i class="fas fa-eye"></i>
                        </button>
                        <button class="btn-icon-action start-chat-btn" title="Open Chat" data-bs-toggle="tooltip">
                            <i class="fas fa-comments"></i>
                        </button>
                    </div>`;
                        }
                    }
                ],
                "order": [[0, 'desc']],
                "pageLength": 10,
                "lengthMenu": [[5, 10, 25, 50], [5, 10, 25, 50]],
                "language": {
                    "emptyTable": '<div class="text-center p-4"><i class="fas fa-question-circle fa-3x text-muted mb-3"></i><br><span class="text-muted">No inquiries found.</span><br><small class="text-secondary">Click "New Inquiry" to submit your first inquiry!</small></div>',
                    "search": '<i class="fas fa-search me-2"></i>',
                    "lengthMenu": 'Show _MENU_ inquiries',
                    "info": 'Showing _START_ to _END_ of _TOTAL_ inquiries',
                    "infoEmpty": 'No inquiries available',
                    "paginate": {
                        "first": '<i class="fas fa-angle-double-left"></i>',
                        "last": '<i class="fas fa-angle-double-right"></i>',
                        "next": '<i class="fas fa-angle-right"></i>',
                        "previous": '<i class="fas fa-angle-left"></i>'
                    }
                },
                "dom": '<"row"<"col-sm-12 col-md-6"l><"col-sm-12 col-md-6"f>>rt<"row"<"col-sm-12 col-md-5"i><"col-sm-12 col-md-7"p>>',
                "responsive": true,
                "processing": false,
                "deferRender": true,
                "stateSave": true,
                "drawCallback": function () {
                    const tooltipTriggerList = [].slice.call(document.querySelectorAll('[data-bs-toggle="tooltip"]'));
                    tooltipTriggerList.map(function (tooltipTriggerEl) {
                        return new bootstrap.Tooltip(tooltipTriggerEl, {
                            trigger: 'hover',
                            delay: { show: 300, hide: 100 }
                        });
                    });
                }
            });

            const inquirySearchBox = inquiriesTableE1.closest('.dataTables_wrapper').find('.dataTables_filter input');
            inquirySearchBox.attr('placeholder', 'Search inquiries...').addClass('form-control-sm');
        }

        initializeTicketSystem();

        const ticketModal = document.getElementById('viewTicketDetailsModal');
        if (ticketModal) {
            ticketModal.addEventListener('hidden.bs.modal', function () {
                setTimeout(() => {
                    const backdrops = document.querySelectorAll('.modal-backdrop');
                    backdrops.forEach(backdrop => backdrop.remove());
                    document.body.classList.remove('modal-open');
                    document.body.style.overflow = '';
                    document.body.style.paddingRight = '';
                }, 50);
            });
        }

        const conversationListPanel = document.getElementById("conversation-list-panel");
        if (conversationListPanel) {
            conversationListPanel.addEventListener('click', (e) => {
                const conversationItem = e.target.closest('.conversation-item');
                if (!conversationItem) return;
                e.preventDefault();
                switchChatContext(conversationItem.dataset);
            });
        }

        if (sendButton) sendButton.addEventListener("click", sendMessage);
        if (messageInput) {
            messageInput.addEventListener("keyup", (e) => {
                updateSendButtonState();
                if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); sendMessage(); }
            });
        }

        if (ticketsDataTable) {
            supportTicketsTableE1.on('click', '.start-chat-btn', function () {
                const rowData = ticketsDataTable.row($(this).parents('tr')).data();
                if (!rowData) {
                    console.error('No row data found for the clicked button.');
                    return
                };

                const route = `ticket/${rowData.subject.toLowerCase().replace(/\s+/g, '-')}`;

                switchChatContext({
                    id: rowData.id,
                    name: `#${rowData.id} - ${rowData.subject}`,
                    route: route
                });
            });

            supportTicketsTableE1.on('click', '.view-details-btn', function () {
                const rowData = ticketsDataTable.row($(this).parents('tr')).data();
                if (!rowData) {
                    console.error('No row data found for view details button');
                    return;
                }

                console.log('Row data for modal:', rowData);

                currentTicketData = rowData;

                populateTicketDetailsModal(rowData);

                const modal = new bootstrap.Modal(document.getElementById('viewTicketDetailsModal'));
                modal.show();
            });
        }

        if (inquiriesDataTable) {
            inquiriesTableE1.on('click', '.start-chat-btn', function () {
                const rowData = inquiriesDataTable.row($(this).parents('tr')).data();
                if (!rowData) return;

                const route = `inquiry/${rowData.topic.toLowerCase().replace(/\s+/g, '-')}`;

                switchChatContext({
                    id: rowData.id,
                    name: `#${rowData.id} - ${rowData.topic}`,
                    route: route
                });
            });

            inquiriesTableE1.on('click', '.view-details-btn', function () {
                const rowData = inquiriesDataTable.row($(this).parents('tr')).data();
                if (!rowData) {
                    console.error('No row data found for inquiry view details button');
                    return;
                }

                console.log('Inquiry row data for modal:', rowData);

                populateInquiryDetailsModal(rowData);

                const modal = new bootstrap.Modal(document.getElementById('viewInquiryDetailsModal'));
                modal.show();
            });
        }
    }

    init();
});
