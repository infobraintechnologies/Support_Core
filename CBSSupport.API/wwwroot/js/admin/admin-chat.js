"use strict";

window.AdminChat = (() => {

    let mainChatContext = {};
    let lastMainChatMessageDate = null;

    const floatingChatContainer = document.getElementById('floating-chat-container');
    const mainChatPanelBody = document.getElementById("chat-panel-body");
    const mainMessageInput = document.getElementById("message-input");
    const mainSendButton = document.getElementById("send-button");
    const mainChatHeader = document.getElementById("chat-header");

    async function initializeChatsPage(currentClientId) {
        if (!currentClientId) {
            renderAdminSidebar({
                groupChats: [],
                privateChats: [],
                internalChats: [],
                ticketChats: [],
                inquiryChats: []
            });
            return;
        }

        $('#group-chats, #private-chats, #internal-chats, #ticket-chats, #inquiry-chats').html(
            '<div class="admin-chat-loading"><div class="spinner-border spinner-border-sm me-2"></div>Loading...</div>'
        );

        try {
            console.log("💬 AdminChat: Loading conversations for client:", currentClientId);
            const response = await fetch(`/v1/api/instructions/sidebar/${currentClientId}`);
            if (!response.ok) {
                throw new Error(`Failed to load sidebar: ${response.statusText}`);
            }

            const sidebarData = await response.json();
            console.log("💬 AdminChat: Sidebar data loaded:", sidebarData);

            renderAdminSidebar(sidebarData);
        } catch (error) {
            console.error('💬 AdminChat: Error loading conversations:', error);
            $('#group-chats, #private-chats, #internal-chats, #ticket-chats, #inquiry-chats').html(
                '<div class="admin-chat-loading text-danger">Error loading chats</div>'
            );
        }
    }

    function initializeChatSidebar() {
        $(document).on('click', '.admin-chat-section-toggle', function (e) {
            e.stopPropagation();
            const target = $(this).data('target');
            const content = $('#' + target);
            const toggle = $(this);

            content.toggleClass('collapsed');
            toggle.toggleClass('expanded');
        });

        $(document).on('click', '#refresh-conversations-btn', function () {
            const currentClientId = window.AdminCore?.getCurrentClientId();
            if (currentClientId) {
                refreshAdminConversations(currentClientId);
            }
        });

        $(document).on('click', '.admin-conversation-item', async function (e) {
            e.preventDefault();
            const $this = $(this);

            const conversationId = $this.data('id');
            const route = $this.data('route');

            if (conversationId === "0" && route === 'support-group') {
                console.log("💬 AdminChat: Creating new group chat");
                try {
                    const currentClientId = window.AdminCore?.getCurrentClientId();
                    const newConversationId = await createGroupChatConversation(currentClientId);
                    $this.attr('data-id', newConversationId);

                    mainChatContext = {
                        id: parseInt(newConversationId, 10),
                        name: $this.data('name'),
                        route: route,
                        type: $this.data('type')
                    };
                } catch (error) {
                    console.error("💬 AdminChat: Failed to create group chat:", error);
                    AdminUtils.showNotification("Failed to create group chat. Please try again.", 'error');
                    return;
                }
            } else {
                const numericId = parseInt(conversationId, 10);

                if (isNaN(numericId)) {
                    console.error("💬 AdminChat: Invalid conversation ID:", conversationId);
                    AdminUtils.showNotification("Invalid conversation ID. Please try again.", 'error');
                    return;
                }

                mainChatContext = {
                    id: numericId,
                    name: $this.data('name'),
                    route: route,
                    type: $this.data('type')
                };
            }

            console.log("💬 AdminChat: Switched to conversation:", mainChatContext);

            $('.admin-conversation-item.active').removeClass('active');
            $this.addClass('active').removeClass('has-unread');

            updateAdminChatHeader(mainChatContext);

            $('#message-input, #send-button').prop('disabled', false);

            try {
                if (window.AdminSignalR) {
                    await window.AdminSignalR.joinConversation(mainChatContext.id);
                    console.log(`💬 AdminChat: Successfully joined SignalR group for conversation ${mainChatContext.id}`);
                }
            } catch (error) {
                console.error(`💬 AdminChat: Failed to join SignalR group for conversation ${mainChatContext.id}:`, error);
            }

            await loadAdminChatMessages(mainChatContext.id);
        });

        if (mainSendButton) {
            $(mainSendButton).on('click', sendMainChatMessage);
        }

        if (mainMessageInput) {
            $(mainMessageInput).on('keyup', e => {
                if (e.key === 'Enter' && !e.shiftKey) {
                    e.preventDefault();
                    sendMainChatMessage();
                }
            });
        }
    }

    function renderAdminSidebar(sidebarData) {
        $('#group-chats, #private-chats, #internal-chats, #ticket-chats, #inquiry-chats').empty();

        if (sidebarData.groupChats && sidebarData.groupChats.length > 0) {
            sidebarData.groupChats.forEach(chat => {
                const chatItem = createAdminConversationItem(chat, 'group');
                $('#group-chats').append(chatItem);
            });
        } else {
            $('#group-chats').html('<div class="admin-chat-loading">No group chat available</div>');
        }

        if (sidebarData.privateChats && sidebarData.privateChats.length > 0) {
            sidebarData.privateChats.forEach(chat => {
                const chatItem = createAdminConversationItem(chat, 'private');
                $('#private-chats').append(chatItem);
            });
        } else {
            $('#private-chats').html('<div class="admin-chat-loading">No private chats</div>');
        }

        if (sidebarData.internalChats && sidebarData.internalChats.length > 0) {
            sidebarData.internalChats.forEach(chat => {
                const chatItem = createAdminConversationItem(chat, 'internal');
                $('#internal-chats').append(chatItem);
            });
        } else {
            $('#internal-chats').html('<div class="admin-chat-loading">No internal chats</div>');
        }

        if (sidebarData.ticketChats && sidebarData.ticketChats.length > 0) {
            sidebarData.ticketChats.forEach(chat => {
                const chatItem = createAdminConversationItem(chat, 'ticket');
                $('#ticket-chats').append(chatItem);
            });
        } else {
            $('#ticket-chats').html('<div class="admin-chat-loading">No ticket chats</div>');
        }

        if (sidebarData.inquiryChats && sidebarData.inquiryChats.length > 0) {
            sidebarData.inquiryChats.forEach(chat => {
                const chatItem = createAdminConversationItem(chat, 'inquiry');
                $('#inquiry-chats').append(chatItem);
            });
        } else {
            $('#inquiry-chats').html('<div class="admin-chat-loading">No inquiry chats</div>');
        }
    }

    function createAdminConversationItem(itemData, type) {
        const avatarClass = getAvatarClass(type);
        const avatarIcon = getAvatarIcon(type, itemData);

        const item = $(`
            <a href="#" class="admin-conversation-item" 
               data-id="${itemData.conversationId}" 
               data-name="${AdminUtils.escapeHtml(itemData.displayName)}" 
               data-type="${type}" 
               data-route="${itemData.route}">
                <div class="d-flex w-100 align-items-center">
                    <div class="admin-avatar-initials ${avatarClass} me-3">
                        ${avatarIcon}
                    </div>
                    <div class="flex-grow-1">
                        <div class="admin-conversation-title">${AdminUtils.escapeHtml(itemData.displayName)}</div>
                        <small class="admin-conversation-subtitle">${AdminUtils.escapeHtml(itemData.subtitle || 'No recent messages')}</small>
                    </div>
                </div>
            </a>
        `);

        return item;
    }

    function getAvatarClass(type) {
        const classes = {
            'private': 'admin-avatar-bg-purple',
            'internal': 'admin-avatar-bg-blue',
            'ticket': 'admin-avatar-bg-orange',
            'inquiry': 'admin-avatar-bg-cyan',
            'group': 'admin-avatar-bg-success'
        };
        return classes[type] || 'admin-avatar-bg-secondary';
    }

    function getAvatarIcon(type, itemData) {
        const icons = {
            'private': '<i class="fas fa-user"></i>',
            'internal': '<i class="fas fa-building"></i>',
            'ticket': '<i class="fas fa-ticket-alt"></i>',
            'inquiry': '<i class="fas fa-question-circle"></i>',
            'group': '<i class="fas fa-users"></i>'
        };
        return icons[type] || (itemData?.avatarInitials || '?');
    }

    async function refreshAdminConversations(currentClientId) {
        if (!currentClientId) return;

        $('#refresh-conversations-btn .fas').addClass('fa-spin');
        $('.admin-chat-loading').show();

        try {
            const response = await fetch(`/v1/api/instructions/sidebar/${currentClientId}`);
            const sidebarData = await response.json();
            renderAdminSidebar(sidebarData);
        } catch (error) {
            console.error('💬 AdminChat: Error refreshing conversations:', error);
        } finally {
            $('#refresh-conversations-btn .fas').removeClass('fa-spin');
            $('.admin-chat-loading').hide();
        }
    }

    function updateAdminChatHeader(context) {
        $('.admin-chat-placeholder').hide();
        $('.admin-chat-title').text(context.name);
        $('.admin-chat-subtitle').text(`${context.type.charAt(0).toUpperCase() + context.type.slice(1)} Chat`);
        $('#chat-info-btn, #chat-settings-btn').show();
    }

    async function loadAdminChatMessages(conversationId) {
        const chatBody = $('#chat-panel-body');
        chatBody.html('<div class="text-center p-4"><div class="spinner-border"></div><div class="mt-2">Loading messages...</div></div>');

        try {
            const numericId = parseInt(conversationId, 10);
            if (isNaN(numericId)) {
                throw new Error(`Invalid conversation ID: ${conversationId}`);
            }

            console.log("💬 AdminChat: Loading messages for conversation ID:", numericId);

            const response = await fetch(`/v1/api/instructions/messages/${numericId}`);
            if (!response.ok) {
                throw new Error(`Failed to load messages: ${response.statusText}`);
            }

            const messages = await response.json();
            console.log("💬 AdminChat: Received messages:", messages);

            if (!Array.isArray(messages)) {
                throw new Error('Expected messages to be an array, received: ' + typeof messages);
            }

            chatBody.empty();
            lastMainChatMessageDate = null;

            if (messages.length === 0) {
                chatBody.html('<div class="text-center text-muted p-4">No messages yet. Start the conversation!</div>');
            } else {
                messages.forEach(msg => displayMainChatMessage(msg, true));
                AdminUtils.scrollToBottom(chatBody[0]);
            }
        } catch (error) {
            console.error('💬 AdminChat: Error loading messages:', error);
            chatBody.html(`<div class="text-center text-danger p-4">Error loading messages: ${error.message}</div>`);
        }
    }

    function addMainChatDateSeparator(msgDateStr) {
        if (!mainChatPanelBody) return;
        const dateStr = new Date(msgDateStr).toDateString();
        if (lastMainChatMessageDate !== dateStr) {
            lastMainChatMessageDate = dateStr;
            const ds = document.createElement('div');
            ds.className = 'date-separator';
            ds.textContent = AdminUtils.formatDateForSeparator(msgDateStr);
            mainChatPanelBody.appendChild(ds);
        }
    }

    function displayMainChatMessage(msg, isHistory = false) {
        if (!mainChatPanelBody) return;

        addMainChatDateSeparator(msg.dateTime);

        const currentUser = window.AdminCore?.getCurrentUser();
        const isSent = msg.insertUser === currentUser?.id;
        const senderName = msg.senderName || (isSent ? currentUser?.name || 'Admin' : 'Client');
        const senderId = msg.insertUser || msg.clientAuthUserId;
        const lastGroup = mainChatPanelBody.lastElementChild;

        const isNewGroup = !lastGroup || lastGroup.dataset.senderId !== String(senderId);

        if (isNewGroup) {
            const group = document.createElement('div');
            group.className = `message-group ${isSent ? 'sent' : 'received'}`;
            group.dataset.senderId = senderId;

            group.innerHTML = `
                <div class="message-cluster">
                    <div class="message-sender">${AdminUtils.escapeHtml(senderName)}</div>
                    <div class="message-bubble">
                        <p class="message-text">${AdminUtils.escapeHtml(msg.instruction || '')}</p>
                        <div class="message-timestamp">${AdminUtils.formatTimestamp(msg.dateTime)}</div>
                    </div>
                </div>
                <div class="chat-avatar">${AdminUtils.escapeHtml(senderName).substring(0, 1)}</div>`;

            mainChatPanelBody.appendChild(group);
        } else {
            const lastCluster = lastGroup.querySelector('.message-cluster');
            if (lastCluster) {
                const bubble = document.createElement('div');
                bubble.className = 'message-bubble';
                bubble.innerHTML = `
                    <p class="message-text">${AdminUtils.escapeHtml(msg.instruction || '')}</p>
                    <div class="message-timestamp">${AdminUtils.formatTimestamp(msg.dateTime)}</div>`;
                lastCluster.appendChild(bubble);
            }
        }

        if (!isHistory) {
            AdminUtils.scrollToBottom(mainChatPanelBody);
        }
    }

    async function sendMainChatMessage() {
        const text = mainMessageInput.value.trim();
        if (!text || !mainChatContext.id) return;

        try {
            const savedMessage = await window.AdminSignalR.sendMessage(mainChatContext.id, text);
            console.log("💬 AdminChat: Message saved successfully:", savedMessage);

            displayMainChatMessage(savedMessage);

            mainMessageInput.value = '';

        } catch (err) {
            console.error("💬 AdminChat: sendMainChatMessage error:", err);
            AdminUtils.showNotification(`Failed to send message: ${err.message}`, 'error');
        }
    }

    async function createGroupChatConversation(clientId) {
        if (!clientId) {
            throw new Error("Client ID is required to create group chat");
        }

        const currentUser = window.AdminCore?.getCurrentUser();
        const initialMessage = {
            Instruction: "Group chat conversation started",
            ClientId: parseInt(clientId),
            InsertUser: currentUser?.id,
            InstCategoryId: 100,
            ServiceId: 3,
            Remarks: "Admin initiated group chat",
            DateTime: new Date().toISOString(),
            Status: true,
            InstChannel: "chat"
        };

        const response = await fetch(`/v1/api/instructions/clients/${clientId}/support-group`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(initialMessage)
        });

        if (!response.ok) {
            throw new Error(`Failed to create group chat: ${response.statusText}`);
        }

        const savedMessage = await response.json();
        return savedMessage.instructionId || savedMessage.id;
    }

    function openEnhancedFloatingChatBox(item, type) {
        const id = item.id;
        const clientName = item.clientName;
        const chatBoxId = `chatbox-${type}-${id}`;

        if (document.getElementById(chatBoxId)) {
            document.getElementById(chatBoxId).classList.remove('collapsed');
            return;
        }

        const title = `#${id} - ${AdminUtils.escapeHtml(item.subject || item.topic)} (${AdminUtils.escapeHtml(clientName)})`;
        const typeIcon = type === 'tkt' ? 'fa-ticket-alt' : 'fa-question-circle';

        const chatBox = document.createElement('div');
        chatBox.className = 'floating-chat-box';
        chatBox.id = chatBoxId;
        chatBox.dataset.id = id;
        chatBox.dataset.type = type;

        chatBox.innerHTML = `
            <div class="chat-box-header">
                <span class="chat-box-title">
                    <i class="fas ${typeIcon} me-2"></i>${title}
                </span>
                <div class="chat-box-actions">
                    <button class="action-minimize" title="Minimize">
                        <i class="fas fa-minus"></i>
                    </button>
                    <button class="action-maximize" title="Open in Main Chat" data-conversation-id="${id}">
                        <i class="fas fa-expand"></i>
                    </button>
                    <button class="action-close" title="Close">
                        <i class="fas fa-times"></i>
                    </button>
                </div>
            </div>
            <div class="chat-box-body"></div>
            <div class="chat-box-footer">
                <textarea class="form-control" rows="1" placeholder="Type your reply..."></textarea>
                <button class="btn btn-primary action-send" title="Send">
                    <i class="fas fa-paper-plane"></i>
                </button>
            </div>`;

        floatingChatContainer.appendChild(chatBox);
        loadAndRenderFloatingChatMessages(id, chatBox.querySelector('.chat-box-body'));
        window.AdminSignalR?.joinConversation(id).catch(error => {
            console.error(`Failed to join floating conversation ${id}:`, error);
        });

        setupFloatingChatEvents(chatBox);
    }

    function setupFloatingChatEvents(chatBox) {
        $(chatBox).find('.action-maximize').on('click', function () {
            const conversationId = $(this).data('conversation-id');
            if (window.AdminNavigation) {
                window.AdminNavigation.navigateToChatsPage();
            }
            setTimeout(() => {
                $(`.admin-conversation-item[data-id="${conversationId}"]`).click();
            }, 100);
            chatBox.remove();
        });

        $(chatBox).on('click', '.action-send', async function () {
            const textarea = chatBox.querySelector('textarea');
            const text = textarea.value.trim();
            if (!text) return;

            const currentUser = window.AdminCore?.getCurrentUser();
            try {
                await window.AdminSignalR.sendMessage(Number(chatBox.dataset.id), text);
                const container = chatBox.querySelector('.chat-box-body');
                const senderName = currentUser?.name || 'Admin';
                const msgRow = document.createElement('div');
                msgRow.className = 'message-row sent';
                msgRow.innerHTML = `
                    <div class="message-content">
                        <div class="message-bubble">
                            <p class="message-text">${AdminUtils.escapeHtml(text)}</p>
                        </div>
                        <span class="message-timestamp">${senderName} - ${AdminUtils.formatTimestamp(new Date())}</span>
                    </div>`;
                container.appendChild(msgRow);
                AdminUtils.scrollToBottom(container);
            } catch (err) {
                console.error('💬 AdminChat: Error sending floating chat message:', err);
                AdminUtils.showNotification('Failed to send message. Please try again.', 'error');
            }

            textarea.value = '';
        });
    }

    async function loadAndRenderFloatingChatMessages(conversationId, container) {
        container.innerHTML = '<div class="text-center p-3"><div class="spinner-border spinner-border-sm"></div></div>';
        try {
            const res = await fetch(`/v1/api/instructions/messages/${conversationId}`);
            if (!res.ok) throw new Error("Failed to load messages");

            const messages = await res.json();
            container.innerHTML = '';

            const currentUser = window.AdminCore?.getCurrentUser();
            messages.forEach(msg => {
                const isSent = msg.insertUser === currentUser?.id;
                const messageClass = isSent ? 'sent' : 'received';
                const senderName = msg.senderName || (isSent ? currentUser?.name || 'Admin' : 'Client');
                container.innerHTML += `
                    <div class="message-row ${messageClass}">
                        <div class="message-content">
                            <div class="message-bubble">
                                <p class="message-text">${AdminUtils.escapeHtml(msg.instruction)}</p>
                            </div>
                            <span class="message-timestamp">${AdminUtils.escapeHtml(senderName)} - ${AdminUtils.formatTimestamp(msg.dateTime)}</span>
                        </div>
                    </div>`;
            });
            AdminUtils.scrollToBottom(container);
        } catch (err) {
            container.innerHTML = '<p class="text-danger p-3">Error loading messages.</p>';
        }
    }

    function handleIncomingMessage(message) {
        const conversationId = message.instructionId;

        console.log(`💬 AdminChat: Processing message for conversation ${conversationId}`);
        console.log(`💬 AdminChat: Current main chat context:`, mainChatContext);

        if (String(mainChatContext.id) === String(conversationId)) {
            console.log(`💬 AdminChat: Message for open main chat #${conversationId}. Calling displayMainChatMessage.`);
            displayMainChatMessage(message);
        } else {
            console.log(`💬 AdminChat: Message for different conversation. mainChatContext.id=${mainChatContext.id}, message conversationId=${conversationId}`);
        }
    }

    async function openChatConversation(instructionId) {
        try {
            let conversationItem = $(`.admin-conversation-item[data-id="${instructionId}"]`);

            if (conversationItem.length === 0) {
                const response = await fetch(`/v1/api/instructions/messages/${instructionId}`);
                if (response.ok) {
                    const messages = await response.json();
                    if (messages.length > 0) {
                        const conversationId = messages[0].instructionId || instructionId;
                        conversationItem = $(`.admin-conversation-item[data-id="${conversationId}"]`);
                    }
                }
            }

            if (conversationItem.length > 0) {
                conversationItem.click();
            } else {
                console.warn(`💬 AdminChat: Could not find conversation for instruction ID: ${instructionId}`);
                AdminUtils.showNotification('Could not open chat conversation', 'error');
            }
        } catch (error) {
            console.error('💬 AdminChat: Error opening chat conversation:', error);
            AdminUtils.showNotification('Failed to open chat conversation', 'error');
        }
    }

    return {
        initializeChatsPage,
        initializeChatSidebar,
        handleIncomingMessage,
        openChatConversation,
        openEnhancedFloatingChatBox,
        refreshAdminConversations
    };
})();
