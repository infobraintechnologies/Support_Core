"use strict";

document.addEventListener('DOMContentLoaded', () => {
    let currentUser = { name: "Admin", id: null };
    let currentClientId = null;
    let currentTicketData = null;
    let currentInquiryData = null;
    const agents = ["Sijal", "Shovan", "Alzeena", "Samana", "Darshan", "Anuj", "Avay", "Nikesh", "Salina", "Safal", "Imon", "Bigya", "Unassigned"];
    const priorities = ["Low", "Normal", "High", "Urgent"];
    let ticketPriorityChart = null;

    let mainChatContext = {};
    let lastMainChatMessageDate = null;
    let ticketsTable = null;
    let inquiriesTable = null;

    let notificationDropdown = null;
    let unreadNotificationCount = 0;
    let notificationPollingInterval = null;

    const floatingChatContainer = document.getElementById('floating-chat-container');
    const conversationListContainer = document.getElementById("conversation-list-container");
    const mainChatPanelBody = document.getElementById("chat-panel-body");
    const mainMessageInput = document.getElementById("message-input");
    const mainSendButton = document.getElementById("send-button");
    const mainChatHeader = document.getElementById("chat-header");

    const connection = new signalR.HubConnectionBuilder()
        .withUrl("/chathub")
        .withAutomaticReconnect()
        .build();

    const formatTimestamp = (d) => new Date(d).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    const escapeHtml = (text) => {
        if (text === null || typeof text === 'undefined') return '';
        const d = document.createElement('div');
        d.textContent = text;
        return d.innerHTML;
    };
    const scrollToBottom = (element) => {
        if (element) {
            element.scrollTop = element.scrollHeight;
        }
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
    const generatePriorityBadge = (priority) => {
        const p = priority ? priority.toLowerCase() : 'normal';
        return `<span class="badge badge-priority-${p}">${escapeHtml(priority || 'Normal')}</span>`;
    };
    const generateStatusBadge = (status) => {
        const s = status ? status.toLowerCase().replace(' ', '-') : 'pending';
        return `<span class="badge badge-status-${s}">${escapeHtml(status || 'Pending')}</span>`;
    };

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

    async function loadNotifications() {
        try {
            const response = await fetch('/v1/api/instructions/notifications/unread');
            if (!response.ok) throw new Error('Failed to load notifications');

            const unreadInstructions = await response.json();

            const cachedReadNotifications = JSON.parse(localStorage.getItem('admin_read_notifications') || '[]');

            const allNotifications = processNotifications(unreadInstructions, cachedReadNotifications);

            updateNotificationBadge(allNotifications.filter(n => !n.isRead).length);
            renderNotifications(allNotifications);

            return allNotifications;
        } catch (error) {
            console.error('Error loading notifications:', error);
            return [];
        }
    }

    function processNotifications(unreadInstructions, cachedReadNotifications = []) {
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
                isRead: instruction.notification_seen_by_admin === 1,
                triggerUserName: instruction.sendername || 'System'
            };

            if (instruction.inst_category_id === 101) {
                notification.type = 'ticket';
                notification.entityType = 'ticket';
                notification.title = 'New Support Ticket';
                notification.message = `New ticket: ${instruction.instruction?.substring(0, 50) || 'General Support'}...`;
            } else if (instruction.inst_category_id === 102) {
                notification.type = 'inquiry';
                notification.entityType = 'inquiry';
                notification.title = 'New Inquiry';
                notification.message = `New inquiry: ${instruction.instruction?.substring(0, 50) || 'General Inquiry'}...`;
            } else if (instruction.inst_category_id === 100) {
                notification.type = 'message';
                notification.entityType = 'message';
                notification.title = 'New Message';
                notification.message = `${instruction.sendername || 'Client'}: ${instruction.instruction?.substring(0, 50) || 'New message'}...`;
                notification.entityId = instruction.instruction_id || instruction.id;
            }

            notification.timeAgo = getTimeAgo(notification.createdAt);
            notification.icon = getNotificationIcon(notification.type);

            notifications.push(notification);
        });

        // 🔧 Add cached read notifications (recently read ones)
        cachedReadNotifications.forEach(cachedNotification => {
            // Only add if not already in the unread list
            if (!notifications.find(n => n.id === cachedNotification.id)) {
                cachedNotification.isRead = true;
                cachedNotification.timeAgo = getTimeAgo(cachedNotification.createdAt);
                notifications.push(cachedNotification);
            }
        });

        // Sort by creation date (newest first)
        notifications.sort((a, b) => new Date(b.createdAt) - new Date(a.createdAt));

        return notifications.slice(0, 20);
    }

    function updateNotificationBadge(count) {
        const badges = [
            'admin-notification-count',
            'admin-notification-count-chats',
            'admin-notification-count-tickets',
            'admin-notification-count-inquiries'
        ];

        badges.forEach(badgeId => {
            const badge = document.getElementById(badgeId);
            if (badge) {
                if (count > 0) {
                    badge.textContent = count > 99 ? '99+' : count.toString();
                    badge.style.display = 'block';

                    if (count > unreadNotificationCount) {
                        const btn = badge.closest('.header-notification-btn');
                        if (btn) {
                            btn.classList.add('notification-shake');
                            setTimeout(() => btn.classList.remove('notification-shake'), 500);
                        }
                    }
                } else {
                    badge.style.display = 'none';
                }
            }
        });

        unreadNotificationCount = count;
    }

    function renderNotifications(notifications) {
        // Try to find the notification list container (could be static or dynamic)
        let container = document.getElementById('admin-notification-list') ||
            document.getElementById('dynamic-notification-list');

        if (!container) {
            console.warn('⚠️ No notification container found');
            return;
        }

        console.log('🔔 Rendering notifications to container:', container.id);

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

        console.log(`🔔 Rendered ${notifications.length} notifications`);
    }

    async function markNotificationAsRead(instructionId) {
        try {
            const response = await fetch(`/v1/api/instructions/${instructionId}/mark-seen-admin`, {
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

                    const existingReadNotifications = JSON.parse(localStorage.getItem('admin_read_notifications') || '[]');
                    const updatedReadNotifications = [notificationData, ...existingReadNotifications].slice(0, 20);
                    localStorage.setItem('admin_read_notifications', JSON.stringify(updatedReadNotifications));
                }

                if (unreadNotificationCount > 0) {
                    updateNotificationBadge(unreadNotificationCount - 1);
                }
            }
        } catch (error) {
            console.error('Error marking notification as read:', error);
        }
    }

    async function markAllNotificationsAsRead() {
        try {
            const response = await fetch('/v1/api/instructions/mark-all-seen-admin', {
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

                const existingReadNotifications = JSON.parse(localStorage.getItem('admin_read_notifications') || '[]');
                const updatedReadNotifications = [...currentNotifications, ...existingReadNotifications]
                    .slice(0, 20); 

                localStorage.setItem('admin_read_notifications', JSON.stringify(updatedReadNotifications));

                document.querySelectorAll('.notification-item.unread').forEach(item => {
                    item.classList.remove('unread');
                });

                updateNotificationBadge(0);
                showNotification('All notifications marked as read', 'success');
            }
        } catch (error) {
            console.error('Error marking all notifications as read:', error);
            showNotification('Failed to mark notifications as read', 'error');
        }
    }

    function initializeNotifications() {
        console.log('🔔 Initializing notifications...');

        const buttonIds = [
            'admin-notification-btn',
            'admin-notification-btn-chats',
            'admin-notification-btn-tickets',
            'admin-notification-btn-inquiries'
        ];

        buttonIds.forEach(btnId => {
            const btn = document.getElementById(btnId);

            if (btn) {
                console.log(`✅ Found button: ${btnId}`);

                // Remove any existing event listeners
                btn.replaceWith(btn.cloneNode(true));
                const newBtn = document.getElementById(btnId);

                newBtn.addEventListener('click', async (e) => {
                    e.preventDefault();
                    e.stopPropagation();

                    console.log(`🔔 Notification button clicked: ${btnId}`);

                    // Close any existing notification menus
                    document.querySelectorAll('.header-notification-dropdown-menu').forEach(menu => {
                        menu.remove();
                    });

                    // 🔧 CREATE NOTIFICATION MENU DYNAMICALLY
                    const notificationMenu = createNotificationMenu();

                    // Position the dropdown
                    if (btnId === 'admin-notification-btn') {
                        // Dashboard button - append to its container
                        const container = newBtn.closest('.header-notification-container');
                        container.appendChild(notificationMenu);
                    } else {
                        // Other page buttons - use fixed positioning
                        document.body.appendChild(notificationMenu);
                        const rect = newBtn.getBoundingClientRect();
                        notificationMenu.style.position = 'fixed';
                        notificationMenu.style.top = `${rect.bottom + 8}px`;
                        notificationMenu.style.right = `${window.innerWidth - rect.right}px`;
                        notificationMenu.style.left = 'auto';
                        notificationMenu.style.zIndex = '1060';
                    }

                    try {
                        await loadNotifications();
                        notificationMenu.classList.add('show');
                        console.log('✅ Notification menu should now be visible');

                        // 🔧 DEBUG: Check if menu is actually visible
                        setTimeout(() => {
                            const menuRect = notificationMenu.getBoundingClientRect();
                            console.log('🔧 Menu dimensions:', {
                                width: menuRect.width,
                                height: menuRect.height,
                                top: menuRect.top,
                                left: menuRect.left,
                                display: window.getComputedStyle(notificationMenu).display,
                                visibility: window.getComputedStyle(notificationMenu).visibility
                            });
                        }, 100);

                    } catch (error) {
                        console.error('❌ Error loading notifications:', error);
                    }
                });

                console.log(`✅ Event listener added to: ${btnId}`);
            } else {
                console.warn(`⚠️ Button not found: ${btnId}`);
            }
        });

        // Close dropdown when clicking outside
        document.addEventListener('click', (e) => {
            if (!e.target.closest('.header-notification-container') &&
                !e.target.closest('.header-notification-btn') &&
                !e.target.closest('.header-notification-dropdown-menu')) {

                document.querySelectorAll('.header-notification-dropdown-menu').forEach(menu => {
                    menu.remove();
                });
            }
        });

        // Handle notification item clicks (we'll set this up when creating the menu)

        // Load initial notifications and start polling
        loadNotifications();

        if (notificationPollingInterval) {
            clearInterval(notificationPollingInterval);
        }
        notificationPollingInterval = setInterval(loadNotifications, 30000);

        console.log('🔔 Notification system initialized successfully');
    }

    // 🔧 NEW FUNCTION: Create notification menu dynamically
    function createNotificationMenu() {
        const menu = document.createElement('div');
        menu.className = 'header-notification-dropdown-menu';
        menu.id = 'dynamic-notification-menu';

        menu.innerHTML = `
        <div class="notification-header">
            <h6>Notifications</h6>
            <button class="btn btn-sm btn-link mark-all-read-btn">Mark all as read</button>
        </div>
        <div class="notification-list" id="dynamic-notification-list">
            <div class="notification-loading">
                <div class="spinner-border spinner-border-sm"></div>
                <span>Loading notifications...</span>
            </div>
        </div>
        <div class="notification-footer">
            <a href="#" class="btn btn-sm btn-primary w-100">View All</a>
        </div>
    `;

        // Add event listeners to the dynamically created menu
        setupNotificationMenuEvents(menu);

        return menu;
    }

    // 🔧 NEW FUNCTION: Setup event listeners for notification menu
    function setupNotificationMenuEvents(menu) {
        // Handle mark all as read
        const markAllBtn = menu.querySelector('.mark-all-read-btn');
        if (markAllBtn) {
            markAllBtn.addEventListener('click', markAllNotificationsAsRead);
        }

        // Handle notification item clicks
        menu.addEventListener('click', async (e) => {
            const notificationItem = e.target.closest('.notification-item');
            if (notificationItem) {
                const notificationId = notificationItem.dataset.id;
                const entityId = notificationItem.dataset.entityId;
                const entityType = notificationItem.dataset.entityType;

                if (notificationItem.classList.contains('unread')) {
                    await markNotificationAsRead(notificationId);
                }

                if (entityType === 'message') {
                    navigateToChatsPage();
                    setTimeout(async () => {
                        await openChatConversation(entityId);
                    }, 500);
                } else if (entityType === 'ticket' && entityId) {
                    navigateToTicketManagement();
                    setTimeout(() => {
                        loadTicketDetails(entityId);
                    }, 500);
                } else if (entityType === 'inquiry' && entityId) {
                    navigateToInquiryManagement();
                    setTimeout(() => {
                        loadInquiryDetails(entityId);
                    }, 500);
                }

                // Close dropdown
                menu.remove();
            }
        });
    }

    function showNotification(message, type = 'info') {
        const toastContainer = document.querySelector('.toast-container') || (() => {
            const container = document.createElement('div');
            container.className = 'toast-container';
            container.style.position = 'fixed';
            container.style.top = '20px';
            container.style.right = '20px';
            container.style.zIndex = '9999';
            document.body.appendChild(container);
            return container;
        })();

        const toast = document.createElement('div');
        toast.className = `toast align-items-center text-white bg-${type === 'success' ? 'success' : type === 'error' ? 'danger' : 'primary'} border-0`;
        toast.setAttribute('role', 'alert');
        toast.innerHTML = `
            <div class="d-flex">
                <div class="toast-body">${message}</div>
                <button type="button" class="btn-close btn-close-white me-2 m-auto" data-bs-dismiss="toast"></button>
            </div>
        `;

        toastContainer.appendChild(toast);
        const toastBootstrap = new bootstrap.Toast(toast);
        toastBootstrap.show();

        toast.addEventListener('hidden.bs.toast', function () {
            toast.remove();
        });
    }

    function initializeAdminChatSidebar() {
        $(document).on('click', '.admin-chat-section-toggle', function (e) {
            e.stopPropagation();
            const target = $(this).data('target');
            const content = $('#' + target);
            const toggle = $(this);

            content.toggleClass('collapsed');
            toggle.toggleClass('expanded');
        });

        $(document).on('click', '#refresh-conversations-btn', function () {
            if (currentClientId) {
                refreshAdminConversations();
            }
        });
    }

    function initializeTicketManagement() {
        const ticketTable = $('#ticketsTable');
        if (ticketTable.length && !$.fn.DataTable.isDataTable('#ticketsTable')) {
            ticketsTable = ticketTable.DataTable({
                "ajax": {
                    "url": "/v1/api/instructions/tickets/all",
                    "dataSrc": "data"
                },
                "columns": [
                    {
                        "data": "id",
                        "title": "ID",
                        "width": "10%",
                        "className": "text-center fw-bold",
                        "render": function (data) {
                            return `<span class="badge bg-light text-dark border">#${data}</span>`;
                        }
                    },
                    {
                        "data": "clientName",
                        "title": "Client",
                        "width": "15%"
                    },
                    {
                        "data": "subject",
                        "title": "Subject",
                        "width": "25%"
                    },
                    {
                        "data": "createdBy",
                        "title": "Created By",
                        "width": "15%"
                    },
                    {
                        "data": "status",
                        "title": "Status",
                        "width": "12%",
                        "className": "text-center",
                        "render": function (data) {
                            return generateStatusBadge(data);
                        }
                    },
                    {
                        "data": "priority",
                        "title": "Priority",
                        "width": "12%",
                        "className": "text-center",
                        "render": function (data) {
                            return generatePriorityBadge(data);
                        }
                    },
                    {
                        "data": null,
                        "title": "Actions",
                        "orderable": false,
                        "width": "11%",
                        "className": "text-center",
                        "render": function () {
                            return `
                                <div class="action-buttons">
                                    <button class="btn-icon-action view-ticket-details-btn" title="View Details">
                                        <i class="fas fa-eye"></i>
                                    </button>
                                    <button class="btn-icon-action start-chat-btn" title="Open Chat">
                                        <i class="fas fa-comments"></i>
                                    </button>
                                </div>`;
                        }
                    }
                ],
                "order": [[0, 'desc']],
                "pageLength": 10,
                "responsive": true,
                "processing": true,
                "language": {
                    "emptyTable": "No tickets found.",
                    "search": '<i class="fas fa-search me-2"></i>',
                },

                "createdRow": function (row, data, dataIndex) {
                    $(row).addClass('clickable-ticket-row');
                    $(row).attr('data-ticket-id', data.id);
                    $(row).attr('title', 'Click to open dedicated chat');
                }
            });

            ticketTable.on('click', '.view-ticket-details-btn', function (e) {
                e.stopPropagation();
                const rowData = ticketsTable.row($(this).parents('tr')).data();
                if (rowData) {
                    loadTicketDetails(rowData.id);
                }
            });

            ticketTable.on('click', '.start-chat-btn', function (e) {
                e.stopPropagation();
                const data = ticketsTable.row($(this).parents('tr')).data();
                if (data) openEnhancedFloatingChatBox(data, 'tkt');
            });

            ticketTable.on('click', 'tbody tr', function (e) {
                if ($(e.target).closest('.action-buttons').length === 0) {
                    const rowData = ticketsTable.row(this).data();
                    if (rowData) {
                        navigateToTicketChat(rowData);
                    }
                }
            });
        }
    }

    function initializeInquiryManagement() {
        const inquiryTable = $('#inquiriesDataTable');
        if (inquiryTable.length && !$.fn.DataTable.isDataTable('#inquiriesDataTable')) {
            inquiriesTable = inquiryTable.DataTable({
                "ajax": {
                    "url": "/v1/api/instructions/inquiries/all",
                    "dataSrc": "data"
                },
                "columns": [
                    {
                        "data": "id",
                        "title": "ID",
                        "width": "10%",
                        "className": "text-center fw-bold",
                        "render": function (data) {
                            return `<span class="badge bg-light text-dark border">#INQ-${data}</span>`;
                        }
                    },
                    {
                        "data": "topic",
                        "title": "Topic",
                        "width": "30%"
                    },
                    {
                        "data": "inquiredBy",
                        "title": "Inquired By",
                        "width": "25%"
                    },
                    {
                        "data": "outcome",
                        "title": "Outcome",
                        "width": "20%",
                        "className": "text-center",
                        "render": function (data) {
                            const status = data || 'Pending';
                            const statusClass = status === 'Completed' ? 'badge-status-completed' : 'badge-status-pending';
                            return `<span class="badge ${statusClass}">${escapeHtml(status)}</span>`;
                        }
                    },
                    {
                        "data": null,
                        "title": "Actions",
                        "orderable": false,
                        "width": "15%",
                        "className": "text-center",
                        "render": function () {
                            return `
                                <div class="action-buttons">
                                    <button class="btn-icon-action view-inquiry-details-btn" title="View Details">
                                        <i class="fas fa-eye"></i>
                                    </button>
                                    <button class="btn-icon-action start-inquiry-chat-btn" title="Open Chat">
                                        <i class="fas fa-comments"></i>
                                    </button>
                                </div>`;
                        }
                    }
                ],
                "order": [[0, 'desc']],
                "pageLength": 10,
                "responsive": true,
                "processing": true,
                "language": {
                    "emptyTable": "No inquiries found.",
                    "search": '<i class="fas fa-search me-2"></i>',
                },
                "createdRow": function (row, data, dataIndex) {
                    $(row).addClass('clickable-inquiry-row');
                    $(row).attr('data-inquiry-id', data.id);
                    $(row).attr('title', 'Click to open dedicated chat');
                }
            });

            inquiryTable.on('click', '.view-inquiry-details-btn', function (e) {
                e.stopPropagation();
                const rowData = inquiriesTable.row($(this).parents('tr')).data();
                if (rowData) {
                    loadInquiryDetails(rowData.id);
                }
            });

            inquiryTable.on('click', '.start-inquiry-chat-btn', function (e) {
                e.stopPropagation();
                const data = inquiriesTable.row($(this).parents('tr')).data();
                if (data) openEnhancedFloatingChatBox(data, 'inq');
            });

            inquiryTable.on('click', 'tbody tr', function (e) {
                if ($(e.target).closest('.action-buttons').length === 0) {
                    const rowData = inquiriesTable.row(this).data();
                    if (rowData) {
                        navigateToInquiryChat(rowData);
                    }
                }
            });
        }
    }

    async function loadTicketDetails(ticketId) {
        try {
            const response = await fetch(`/v1/api/instructions/tickets/${ticketId}/details`);
            if (!response.ok) throw new Error('Failed to load ticket details');

            const ticket = await response.json();
            currentTicketData = ticket;

            console.log('Loaded ticket data:', ticket);

            $('#detail-ticket-id').text(`#TKT-${ticket.id}`);
            $('#detail-ticket-subject').text(ticket.subject || 'General Support');

            $('#detail-ticket-status').val(ticket.status || 'Open');

            $('#detail-ticket-priority').val(ticket.priority || 'Normal');
            $('#detail-ticket-created-by').val(ticket.createdBy || 'Unknown');
            $('#detail-ticket-client').val(ticket.clientName || 'Unknown');
            $('#detail-ticket-date').val(new Date(ticket.date).toLocaleString());
            $('#detail-ticket-description').val(ticket.description || 'No description available.');
            $('#detail-ticket-resolved-by').val(ticket.resolvedBy || '');

            if (ticket.resolvedDate) {
                $('#detail-ticket-resolved-date').val(new Date(ticket.resolvedDate).toLocaleString());
            } else {
                $('#detail-ticket-resolved-date').val('');
            }

            $('#ticket-detail-placeholder').hide();
            $('#ticket-detail-content').show();

            $('.ticket-properties').scrollTop(0);

            console.log('Ticket details loaded successfully');

        } catch (error) {
            console.error('Error loading ticket details:', error);
            showNotification('Failed to load ticket details. Please try again.', 'error');
        }
    }

    async function loadInquiryDetails(inquiryId) {
        try {
            const response = await fetch(`/v1/api/instructions/inquiries/${inquiryId}/details`);
            if (!response.ok) throw new Error('Failed to load inquiry details');

            const inquiry = await response.json();
            currentInquiryData = inquiry;

            $('#detail-inquiry-id').text(`#INQ-${inquiry.id}`);
            $('#detail-inquiry-topic').text(inquiry.topic);
            $('#detail-inquiry-outcome').val(inquiry.outcome);
            $('#detail-inquiry-inquired-by').val(inquiry.inquiredBy);
            $('#detail-inquiry-date').val(new Date(inquiry.date).toLocaleString());
            $('#detail-inquiry-description').val(inquiry.description || 'No description provided.');

            $('#inquiry-detail-placeholder').hide();
            $('#inquiry-detail-content').show();

        } catch (error) {
            console.error('Error loading inquiry details:', error);
            showNotification('Failed to load inquiry details. Please try again.', 'error');
        }
    }

    async function updateTicketStatus() {
        if (!currentTicketData) {
            console.error('No current ticket data for status update');
            showNotification('No ticket selected for status update.', 'error');
            return;
        }

        const newStatus = $('#detail-ticket-status').val();
        const isCompleted = newStatus === 'Resolved';

        console.log(`Updating ticket ${currentTicketData.id} to status: ${newStatus}`);

        try {

            const updateBtn = $('#btn-update-ticket');
            updateBtn.prop('disabled', true).html('<i class="fas fa-spinner fa-spin"></i> Updating...');

            const response = await fetch(`/v1/api/instructions/tickets/${currentTicketData.id}/status`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ isCompleted: isCompleted })
            });

            if (!response.ok) {
                const errorData = await response.json().catch(() => ({ message: 'Unknown error' }));
                throw new Error(errorData.message || `HTTP ${response.status}: ${response.statusText}`);
            }

            const result = await response.json();
            console.log('Update response:', result);

            if (result.success) {
                currentTicketData.status = newStatus;
                if (isCompleted) {
                    currentTicketData.resolvedDate = new Date().toISOString();
                    currentTicketData.resolvedBy = currentUser.name || 'Admin';
                } else {
                    currentTicketData.resolvedDate = null;
                    currentTicketData.resolvedBy = null;
                }

                if (currentTicketData.resolvedDate) {
                    $('#detail-ticket-resolved-date').val(new Date(currentTicketData.resolvedDate).toLocaleString());
                    $('#detail-ticket-resolved-by').val(currentTicketData.resolvedBy);
                } else {
                    $('#detail-ticket-resolved-date').val('');
                    $('#detail-ticket-resolved-by').val('');
                }

                if (ticketsTable) {
                    ticketsTable.ajax.reload(null, false);
                }

                showNotification(`Ticket status updated to ${newStatus} successfully!`, 'success');
            } else {
                throw new Error(result.message || 'Update failed');
            }

        } catch (error) {
            console.error('Error updating ticket status:', error);
            showNotification(`Failed to update ticket status: ${error.message}`, 'error');
        } finally {
            const updateBtn = $('#btn-update-ticket');
            updateBtn.prop('disabled', false);
            updateBtn.html('<i class="fas fa-save"></i> Update Status');
        }
    }

    async function updateInquiryStatus() {
        if (!currentInquiryData) return;

        const newOutcome = $('#detail-inquiry-outcome').val();
        const isCompleted = newOutcome === 'Completed';

        try {
            const response = await fetch(`/v1/api/instructions/inquiries/${currentInquiryData.id}/status`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ isCompleted: isCompleted })
            });

            if (!response.ok) throw new Error('Failed to update inquiry status');

            const result = await response.json();

            if (result.success) {
                currentInquiryData.outcome = newOutcome;

                if (inquiriesTable) {
                    inquiriesTable.ajax.reload();
                }

                showNotification('Inquiry status updated successfully!', 'success');
            } else {
                throw new Error(result.message || 'Update failed');
            }

        } catch (error) {
            console.error('Error updating inquiry status:', error);
            showNotification(`Failed to update inquiry status: ${error.message}`, 'error');
        }
    }

    function closeTicketDetail() {
        currentTicketData = null;
        $('#ticket-detail-content').hide();
        $('#ticket-detail-placeholder').show();
    }

    function closeInquiryDetail() {
        currentInquiryData = null;
        $('#inquiry-detail-content').hide();
        $('#inquiry-detail-placeholder').show();
    }

    function navigateToTicketManagement(statusFilter = null) {
        $('.admin-sidebar .nav-link.active').removeClass('active');
        $('.admin-sidebar .nav-link[data-page="ticket-management"]').addClass('active');
        $('.admin-page.active').removeClass('active');
        $('#ticket-management-page').addClass('active');

        pageInitializers['ticket-management']();

        if (statusFilter && ticketsTable) {
            setTimeout(() => {
                ticketsTable.column(4).search(statusFilter).draw();
            }, 100);
        }
    }

    async function navigateToTicketChat(ticketData) {
        console.log('Navigating to ticket chat for:', ticketData);

        if (ticketData.clientName) {

            const clientOption = $('.client-switcher option').filter(function () {
                return $(this).text() === ticketData.clientName;
            });

            if (clientOption.length > 0) {
                const clientId = clientOption.val();
                currentClientId = clientId;
                $('.client-switcher').val(clientId);
            }
        }

        navigateToChatsPage();

        setTimeout(async () => {
            try {
                let conversationItem = $(`.admin-conversation-item[data-id="${ticketData.id}"]`);

                if (conversationItem.length === 0) {
                    await refreshAdminConversations();

                    setTimeout(() => {
                        conversationItem = $(`.admin-conversation-item[data-id="${ticketData.id}"]`);

                        if (conversationItem.length > 0) {
                            conversationItem.click();
                            showNotification(`Opened chat for Ticket #${ticketData.id}`, 'success');
                        } else {
                            showNotification(`Chat conversation for Ticket #${ticketData.id} not found. Please use the floating chat instead.`, 'warning');
                        }
                    }, 1000);
                } else {
                    conversationItem.click();
                    showNotification(`Opened chat for Ticket #${ticketData.id}`, 'success');
                }
            } catch (error) {
                console.error('Error navigating to ticket chat:', error);
                showNotification('Failed to open ticket chat. Please try the floating chat instead.', 'error');
            }
        }, 500);
    }

    async function navigateToInquiryChat(inquiryData) {
        console.log('Navigating to inquiry chat for:', inquiryData);

        if (inquiryData.clientName) {
            const clientOption = $('.client-switcher option').filter(function () {
                return $(this).text() === inquiryData.clientName;
            });

            if (clientOption.length > 0) {
                const clientId = clientOption.val();
                currentClientId = clientId;
                $('.client-switcher').val(clientId);
            }
        }

        navigateToChatsPage();

        setTimeout(async () => {
            try {
                let conversationItem = $(`.admin-conversation-item[data-id="${inquiryData.id}"]`);

                if (conversationItem.length === 0) {
                    await refreshAdminConversations();

                    setTimeout(() => {
                        conversationItem = $(`.admin-conversation-item[data-id="${inquiryData.id}"]`);

                        if (conversationItem.length > 0) {
                            conversationItem.click();
                            showNotification(`Opened chat for Inquiry #${inquiryData.id}`, 'success');
                        } else {
                            showNotification(`Chat conversation for Inquiry #${inquiryData.id} not found. Please use the floating chat instead.`, 'warning');
                        }
                    }, 1000);
                } else {
                    conversationItem.click();
                    showNotification(`Opened chat for Inquiry #${inquiryData.id}`, 'success');
                }
            } catch (error) {
                console.error('Error navigating to inquiry chat:', error);
                showNotification('Failed to open inquiry chat. Please try the floating chat instead.', 'error');
            }
        }, 500);
    }

    function navigateToInquiryManagement(statusFilter = null) {
        $('.admin-sidebar .nav-link.active').removeClass('active');
        $('.admin-sidebar .nav-link[data-page="inquiry-management"]').addClass('active');
        $('.admin-page.active').removeClass('active');
        $('#inquiry-management-page').addClass('active');

        pageInitializers['inquiry-management']();

        if (statusFilter && inquiriesTable) {
            setTimeout(() => {
                inquiriesTable.column(3).search(statusFilter).draw();
            }, 100);
        }
    }

    // Add these functions after navigateToInquiryManagement function

    function navigateToChatsPage() {
        $('.admin-sidebar .nav-link.active').removeClass('active');
        $('.admin-sidebar .nav-link[data-page="chats"]').addClass('active');
        $('.admin-page.active').removeClass('active');
        $('#chats-page').addClass('active');

        pageInitializers['chats']();
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
                console.warn(`Could not find conversation for instruction ID: ${instructionId}`);
                showNotification('Could not open chat conversation', 'error');
            }
        } catch (error) {
            console.error('Error opening chat conversation:', error);
            showNotification('Failed to open chat conversation', 'error');
        }
    }

    async function createGroupChatConversation(clientId) {
        if (!clientId) {
            throw new Error("Client ID is required to create group chat");
        }

        const initialMessage = {
            Instruction: "Group chat conversation started",
            ClientId: parseInt(clientId),
            InsertUser: currentUser.id,
            InstCategoryId: 100,
            ServiceId: 3,
            Remarks: "Admin initiated group chat",
            DateTime: new Date().toISOString(),
            Status: true,
            InstChannel: "chat"
        };

        const response = await fetch('/v1/api/instructions/support-group', {
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
               data-name="${escapeHtml(itemData.displayName)}" 
               data-type="${type}" 
               data-route="${itemData.route}">
                <div class="d-flex w-100 align-items-center">
                    <div class="admin-avatar-initials ${avatarClass} me-3">
                        ${avatarIcon}
                    </div>
                    <div class="flex-grow-1">
                        <div class="admin-conversation-title">${escapeHtml(itemData.displayName)}</div>
                        <small class="admin-conversation-subtitle">${escapeHtml(itemData.subtitle || 'No recent messages')}</small>
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

    async function refreshAdminConversations() {
        if (!currentClientId) return;

        $('#refresh-conversations-btn .fas').addClass('fa-spin');
        $('.admin-chat-loading').show();

        try {
            const response = await fetch(`/v1/api/instructions/sidebar/${currentClientId}`);
            const sidebarData = await response.json();
            renderAdminSidebar(sidebarData);
        } catch (error) {
            console.error('Error refreshing conversations:', error);
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

            console.log("ADMIN: Loading messages for conversation ID:", numericId);

            const response = await fetch(`/v1/api/instructions/messages/${numericId}`);
            if (!response.ok) {
                throw new Error(`Failed to load messages: ${response.statusText}`);
            }

            const messages = await response.json();
            console.log("ADMIN: Received messages:", messages);

            if (!Array.isArray(messages)) {
                throw new Error('Expected messages to be an array, received: ' + typeof messages);
            }

            chatBody.empty();
            lastMainChatMessageDate = null;

            if (messages.length === 0) {
                chatBody.html('<div class="text-center text-muted p-4">No messages yet. Start the conversation!</div>');
            } else {
                messages.forEach(msg => displayMainChatMessage(msg, true));
                scrollToBottom(chatBody[0]);
            }
        } catch (error) {
            console.error('Error loading messages:', error);
            chatBody.html(`<div class="text-center text-danger p-4">Error loading messages: ${error.message}</div>`);
        }
    }

    function openEnhancedFloatingChatBox(item, type) {
        const id = item.id;
        const clientName = item.clientName;
        const chatBoxId = `chatbox-${type}-${id}`;

        if (document.getElementById(chatBoxId)) {
            document.getElementById(chatBoxId).classList.remove('collapsed');
            return;
        }

        const title = `#${id} - ${escapeHtml(item.subject || item.topic)} (${escapeHtml(clientName)})`;
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

        $(chatBox).find('.action-maximize').on('click', function () {
            const conversationId = $(this).data('conversation-id');
            $('[data-page="chats"]').click();
            setTimeout(() => {
                $(`.admin-conversation-item[data-id="${conversationId}"]`).click();
            }, 100);
            chatBox.remove();
        });
    }

    async function loadAndRenderFloatingChatMessages(conversationId, container) {
        container.innerHTML = '<div class="text-center p-3"><div class="spinner-border spinner-border-sm"></div></div>';
        try {
            const res = await fetch(`/v1/api/instructions/messages/${conversationId}`);
            if (!res.ok) throw new Error("Failed to load messages");
            const messages = await res.json();
            container.innerHTML = '';
            messages.forEach(msg => {
                const isSent = msg.insertUser === currentUser.id;
                const messageClass = isSent ? 'sent' : 'received';
                const senderName = msg.senderName || (isSent ? currentUser.name : 'Client');
                container.innerHTML += `<div class="message-row ${messageClass}"><div class="message-content"><div class="message-bubble"><p class="message-text">${escapeHtml(msg.instruction)}</p></div><span class="message-timestamp">${escapeHtml(senderName)} - ${formatTimestamp(msg.dateTime)}</span></div></div>`
            });
            scrollToBottom(container);
        } catch (err) {
            container.innerHTML = '<p class="text-danger p-3">Error loading messages.</p>';
        }
    }

    function addMainChatDateSeparator(msgDateStr) {
        if (!mainChatPanelBody) return;
        const dateStr = new Date(msgDateStr).toDateString();
        if (lastMainChatMessageDate !== dateStr) {
            lastMainChatMessageDate = dateStr;
            const ds = document.createElement('div');
            ds.className = 'date-separator';
            ds.textContent = formatDateForSeparator(msgDateStr);
            mainChatPanelBody.appendChild(ds);
        }
    }

    function displayMainChatMessage(msg, isHistory = false) {
        if (!mainChatPanelBody) return;

        addMainChatDateSeparator(msg.dateTime);

        const isSent = msg.insertUser === currentUser.id;
        const senderName = msg.senderName || (isSent ? currentUser.name : 'Client');
        const senderId = msg.insertUser || msg.clientAuthUserId;
        const lastGroup = mainChatPanelBody.lastElementChild;

        const isNewGroup = !lastGroup || lastGroup.dataset.senderId !== String(senderId);

        if (isNewGroup) {
            const group = document.createElement('div');
            group.className = `message-group ${isSent ? 'sent' : 'received'}`;
            group.dataset.senderId = senderId;

            group.innerHTML = `
            <div class="message-cluster">
                <div class="message-sender">${escapeHtml(senderName)}</div>
                <div class="message-bubble">
                    <p class="message-text">${escapeHtml(msg.instruction || '')}</p>
                    <div class="message-timestamp">${formatTimestamp(msg.dateTime)}</div>
                </div>
            </div>
            <div class="chat-avatar">${escapeHtml(senderName).substring(0, 1)}</div>`;

            mainChatPanelBody.appendChild(group);
        } else {
            const lastCluster = lastGroup.querySelector('.message-cluster');
            if (lastCluster) {
                const bubble = document.createElement('div');
                bubble.className = 'message-bubble';
                bubble.innerHTML = `
                <p class="message-text">${escapeHtml(msg.instruction || '')}</p>
                <div class="message-timestamp">${formatTimestamp(msg.dateTime)}</div>`;
                lastCluster.appendChild(bubble);
            }
        }

        if (!isHistory) {
            scrollToBottom(mainChatPanelBody);
        }
    }

    async function sendMainChatMessage() {
        const text = mainMessageInput.value.trim();
        if (!text || !mainChatContext.id) return;

        let postUrl;
        if (mainChatContext.route === 'support-group') {
            postUrl = "/v1/api/instructions/support-group";
        } else {
            postUrl = "/v1/api/instructions/reply";
        }

        const payload = {
            Instruction: text,
            InstructionId: parseInt(mainChatContext.id, 10),
            ClientId: currentClientId,
            InsertUser: currentUser.id,
            InstCategoryId: 100,
            ServiceId: 3,
            Remarks: "Message from admin panel"
        };

        console.log("ADMIN: Sending message to", postUrl, "with payload:", payload);

        try {
            const response = await fetch(postUrl, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(payload)
            });

            if (!response.ok) {
                const errorData = await response.json().catch(() => ({ message: "Unknown API error" }));
                throw new Error(errorData.message || "API call failed");
            }

            const savedMessage = await response.json();
            console.log("ADMIN: Message saved successfully:", savedMessage);

            displayMainChatMessage(savedMessage);
            await connection.invoke("SendAdminMessage", savedMessage);

            mainMessageInput.value = '';

        } catch (err) {
            console.error("sendMainChatMessage error:", err);
            alert(`Failed to send message: ${err.message}`);
        }
    }

    // 🔥 ADD: New functions for enhanced dashboard

    // Enhanced dashboard initialization
    async function loadEnhancedDashboardData() {
        try {
            if (!currentClientId) {
                // Load aggregate data for all clients
                const [statsRes, ticketsRes, inquiriesRes] = await Promise.all([
                    fetch('/v1/api/dashboard/stats/all'),
                    fetch('/v1/api/instructions/tickets/all'),
                    fetch('/v1/api/instructions/inquiries/all')
                ]);

                if (!statsRes.ok || !ticketsRes.ok || !inquiriesRes.ok) {
                    throw new Error("Failed to fetch dashboard data");
                }

                const stats = await statsRes.json();
                const allTickets = await ticketsRes.json();
                const allInquiries = await inquiriesRes.json();

                const ticketData = allTickets.data || [];
                const inquiryData = allInquiries.data || [];

                // Update basic stats
                updateBasicStats(stats, ticketData, inquiryData);
                
                // Update urgent sections
                updateUrgentSections(ticketData, inquiryData);
                
                // Load detailed unsolved items
                await loadUnsolvedItems(ticketData, inquiryData);
                
                // Update charts
                updatePriorityChart(ticketData);
                
                // Load recent activity
                loadRecentActivity(ticketData);

            } else {
                // Load data for specific client
                await loadClientSpecificDashboard();
            }
        } catch (error) {
            console.error('Error loading enhanced dashboard data:', error);
            showNotification('Failed to load dashboard data', 'error');
        }
    }

    function updateBasicStats(stats, ticketData, inquiryData) {
        $('#stat-total-tickets').text(stats.totalTickets || ticketData.length);
        $('#stat-open-tickets').text(stats.openTickets || ticketData.filter(t => t.status !== 'Resolved').length);
        $('#stat-resolved-tickets').text(stats.resolvedTickets || ticketData.filter(t => t.status === 'Resolved').length);
        $('#stat-total-inquiries').text(stats.totalInquiries || inquiryData.length);
        
        const solvedInquiries = inquiryData.filter(i => i.outcome === 'Completed').length;
        const unsolvedInquiries = inquiryData.filter(i => i.outcome === 'Pending').length;
        
        $('#stat-solved-inquiries').text(solvedInquiries);
        $('#stat-unsolved-inquiries').text(unsolvedInquiries);
    }

    function updateUrgentSections(ticketData, inquiryData) {
        const unsolvedTickets = ticketData.filter(t => t.status !== 'Resolved').length;
        const unsolvedInquiries = inquiryData.filter(i => i.outcome === 'Pending').length;
        const totalUnsolved = unsolvedTickets + unsolvedInquiries;

        // Update urgent alert
        $('#total-unsolved-count').text(totalUnsolved);
        $('#urgent-tickets-count').text(unsolvedTickets);
        $('#urgent-inquiries-count').text(unsolvedInquiries);

        // Update badges
        $('#critical-tickets-badge').text(`${unsolvedTickets} Critical`);
        $('#pending-inquiries-badge').text(`${unsolvedInquiries} Pending`);
        $('#urgent-tickets-badge').text(unsolvedTickets > 0 ? 'URGENT' : 'CLEAR');
        $('#urgent-inquiries-badge').text(unsolvedInquiries > 0 ? 'PENDING' : 'CLEAR');

        // Show/hide urgent alert based on count
        const alertElement = $('.alert-warning').parent();
        if (totalUnsolved > 0) {
            alertElement.show();
            
            // Add pulsing effect for high counts
            if (totalUnsolved > 10) {
                alertElement.addClass('urgent-pulse');
            }
        } else {
            alertElement.hide();
        }
    }

    async function loadUnsolvedItems(ticketData, inquiryData) {
        const unsolvedTickets = ticketData.filter(t => t.status !== 'Resolved')
                                    .sort((a, b) => new Date(b.date) - new Date(a.date))
                                    .slice(0, 5);
    
        const unsolvedInquiries = inquiryData.filter(i => i.outcome === 'Pending')
                                        .sort((a, b) => new Date(b.date) - new Date(a.date))
                                        .slice(0, 5);

        renderUnsolvedTickets(unsolvedTickets);
        renderUnsolvedInquiries(unsolvedInquiries);
    }

    function renderUnsolvedTickets(tickets) {
        const container = $('#unsolved-tickets-list');
        
        if (tickets.length === 0) {
            container.html(`
                <div class="text-center text-success p-4">
                    <i class="fas fa-check-circle fa-3x mb-3"></i>
                    <h6>🎉 No Critical Tickets!</h6>
                    <p class="text-muted mb-0">All tickets have been resolved.</p>
                </div>
            `);
            return;
        }

        const ticketsHtml = tickets.map(ticket => {
            const timeAgo = getTimeAgo(ticket.date);
            const priorityClass = ticket.priority ? ticket.priority.toLowerCase() : 'normal';
            
            return `
                <div class="unsolved-ticket-item" onclick="navigateToTicketDetails(${ticket.id})">
                    <div class="unsolved-item-header">
                        <div>
                            <div class="unsolved-item-title">#${ticket.id} - ${escapeHtml(ticket.subject || 'General Support')}</div>
                            <div class="unsolved-item-meta">
                                <strong>${escapeHtml(ticket.clientName || 'Unknown Client')}</strong> • 
                                Created by ${escapeHtml(ticket.createdBy || 'Unknown')}
                            </div>
                        </div>
                        <div class="unsolved-item-priority">
                            ${generatePriorityBadge(ticket.priority)}
                        </div>
                    </div>
                    <div class="unsolved-item-time">
                        <i class="fas fa-clock me-1"></i>${timeAgo}
                        ${ticket.priority === 'Urgent' ? '<span class="badge bg-danger ms-2">🔥 CRITICAL</span>' : ''}
                    </div>
                </div>
            `;
        }).join('');

        container.html(ticketsHtml);
    }

    function renderUnsolvedInquiries(inquiries) {
        const container = $('#unsolved-inquiries-list');
        
        if (inquiries.length === 0) {
            container.html(`
                <div class="text-center text-success p-4">
                    <i class="fas fa-check-circle fa-3x mb-3"></i>
                    <h6>✅ No Pending Inquiries!</h6>
                    <p class="text-muted mb-0">All inquiries have been addressed.</p>
                </div>
            `);
            return;
        }

        const inquiriesHtml = inquiries.map(inquiry => {
            const timeAgo = getTimeAgo(inquiry.date);
            
            return `
                <div class="unsolved-inquiry-item" onclick="navigateToInquiryDetails(${inquiry.id})">
                    <div class="unsolved-item-header">
                        <div>
                            <div class="unsolved-item-title">#INQ-${inquiry.id} - ${escapeHtml(inquiry.topic || 'General Inquiry')}</div>
                            <div class="unsolved-item-meta">
                                <strong>${escapeHtml(inquiry.clientName || 'Unknown Client')}</strong> • 
                                By ${escapeHtml(inquiry.inquiredBy || 'Unknown')}
                            </div>
                        </div>
                        <div class="unsolved-item-priority">
                            <span class="badge bg-warning">PENDING</span>
                        </div>
                    </div>
                    <div class="unsolved-item-time">
                        <i class="fas fa-clock me-1"></i>${timeAgo}
                    </div>
                </div>
            `;
        }).join('');

        container.html(inquiriesHtml);
    }

    // Navigation functions for unsolved items
    function navigateToTicketDetails(ticketId) {
        navigateToTicketManagement();
        setTimeout(() => {
            loadTicketDetails(ticketId);
        }, 500);
    }

    function navigateToInquiryDetails(inquiryId) {
        navigateToInquiryManagement();
        setTimeout(() => {
            loadInquiryDetails(inquiryId);
        }, 500);
    }

    function loadRecentActivity(ticketData) {
        const recentTicketsList = $('#recent-tickets-list');
        recentTicketsList.empty();
        
        if (ticketData.length > 0) {
            const recentTickets = ticketData.slice(0, 5);
            recentTickets.forEach(ticket => {
                const lastUpdate = new Date(ticket.date).toLocaleString([], { year: 'numeric', month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit' });
                const statusIcon = ticket.status === 'Resolved' ? 'fa-check-circle text-success' : 'fa-clock text-warning';
                
                const itemHtml = `
                    <div class="recent-ticket-item" onclick="navigateToTicketDetails(${ticket.id})" style="cursor: pointer;">
                        <div class="recent-ticket-info">
                            <div class="d-flex align-items-center mb-1">
                                <i class="fas ${statusIcon} me-2"></i>
                                <strong>#${ticket.id} - ${escapeHtml(ticket.clientName)}</strong>
                            </div>
                            <small>Subject: ${escapeHtml(ticket.subject)} | Last Update: ${lastUpdate}</small>
                        </div>
                        ${generatePriorityBadge(ticket.priority)}
                    </div>`;
                recentTicketsList.append(itemHtml);
            });
        } else {
            recentTicketsList.html('<p class="text-muted p-3">No recent tickets found.</p>');
        }
    }

    // 🔧 REPLACE: Clean up the pageInitializers object (find and replace the existing one)
    // 🔧 REPLACE: Clean up the pageInitializers object (find and replace the existing one)
    // 🔧 REPLACE: Clean up the pageInitializers object (find and replace the existing one)
    // 🔧 REPLACE: Clean up the pageInitializers object (find and replace the existing one)
    const pageInitializers = {
        dashboard: async function () {
            console.log("🎯 Loading Enhanced Dashboard...");
            try {
                await loadEnhancedDashboardData();
            } catch (error) {
                console.error("Dashboard initialization error:", error);
                showNotification('Failed to initialize dashboard', 'error');
            }
            try {
                await loadEnhancedDashboardData();
            } catch (error) {
                console.error("Dashboard initialization error:", error);
                showNotification('Failed to initialize dashboard', 'error');
            }
            try {
                await loadEnhancedDashboardData();
            } catch (error) {
                console.error("Dashboard initialization error:", error);
                showNotification('Failed to initialize dashboard', 'error');
            }
            try {
                await loadEnhancedDashboardData();
            } catch (error) {
                console.error("Dashboard initialization error:", error);
                showNotification('Failed to initialize dashboard', 'error');
            }
            try {
                await loadEnhancedDashboardData();
            } catch (error) {
                console.error("Dashboard initialization error:", error);
                showNotification('Failed to initialize dashboard', 'error');
            }
        },





        chats: async function () {
            console.log("ADMIN: Initializing chats page for client:", currentClientId);

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

            $('#group-chats, #private-charts, #internal-charts, #ticket-charts, #inquiry-charts').html('<div class="admin-chat-loading"><div class="spinner-border spinner-border-sm me-2"></div>Loading...</div>');
            $('#group-charts, #private-charts, #internal-charts, #ticket-charts, #inquiry-charts').html('<div class="admin-chat-loading"><div class="spinner-border spinner-border-sm me-2"></div>Loading...</div>');
            $('#group-charts, #private-charts, #internal-charts, #ticket-charts, #inquiry-charts').html('<div class="admin-chat-loading"><div class="spinner-border spinner-border-sm me-2"></div>Loading...</div>');
            $('#group-charts, #private-charts, #internal-charts, #ticket-charts, #inquiry-charts').html('<div class="admin-chat-loading"><div class="spinner-border spinner-border-sm me-2"></div>Loading...</div>');
            $('#group-charts, #private-charts, #internal-charts, #ticket-charts, #inquiry-charts').html('<div class="admin-chat-loading"><div class="spinner-border spinner-border-sm me-2"></div>Loading...</div>');
            $('#group-charts, #private-charts, #internal-charts, #ticket-charts, #inquiry-charts').html('<div class="admin-chat-loading"><div class="spinner-border spinner-border-sm me-2"></div>Loading...</div>');

            try {
                console.log("ADMIN: Loading conversations for client:", currentClientId);
                const response = await fetch(`/v1/api/instructions/sidebar/${currentClientId}`);
                if (!response.ok) {
                    throw new Error(`Failed to load sidebar: ${response.statusText}`);
                }

                const sidebarData = await response.json();
                console.log("ADMIN: Sidebar data loaded:", sidebarData);

                renderAdminSidebar(sidebarData);
            } catch (error) {
                console.error('Error loading conversations:', error);
                $('#group-charts, #private-charts, #internal-charts, #ticket-charts, #inquiry-charts').html('<div class="admin-chat-loading text-danger">Error loading chats</div>');
            }
        },

        'ticket-management': function () {
            initializeTicketManagement();
        },

        'inquiry-management': function () {
            initializeInquiryManagement();
        }
    };

    function wireEvents() {
        $('.admin-sidebar .nav-link').on('click', function (e) {
            e.preventDefault();
            const pageName = $(this).data('page');

            $('.admin-sidebar .nav-link.active').removeClass('active');
            $(this).addClass('active');
            $('.admin-page.active').removeClass('active');
            $('#' + pageName + '-page').addClass('active');

            if (pageName === 'chats') {
                const chatsNavLink = document.querySelector('[data-page="chats"]');
                if (chatsNavLink) {
                    chatsNavLink.classList.remove('has-notification');
                    const badge = chatsNavLink.querySelector('.notification-badge');
                    if (badge) badge.remove();
                }
            }

            if (pageInitializers[pageName]) {
                pageInitializers[pageName]();
            }
        });

        $('.client-switcher').on('change', function () {
            const selectedClientId = $(this).val();
            currentClientId = selectedClientId;

            $('.client-switcher').val(selectedClientId);

            const activePage = $('.admin-sidebar .nav-link.active').data('page');

            if (activePage === 'dashboard' || activePage === 'chats') {
                pageInitializers[activePage]();
            }

            if (ticketsTable) {
                const clientName = $(this).find('option:selected').text();
                const searchTerm = selectedClientId ? `^${clientName}$` : '';
                ticketsTable.column(1).search(searchTerm, true, false).draw();
            }
            if (inquiriesTable) {
                const clientName = $(this).find('option:selected').text();
                const searchTerm = selectedClientId ? `^${clientName}$` : '';
                inquiriesTable.column(1).search(searchTerm, true, false).draw();
            }
        });

        initializeAdminChatSidebar();

        $(document).on('click', '.admin-conversation-item', async function (e) {
            e.preventDefault();
            const $this = $(this);

            const conversationId = $this.data('id');
            const route = $this.data('route');

            if (conversationId === "0" && route === 'support-group') {
                console.log("ADMIN: Creating new group chat for client:", currentClientId);
                try {
                    const newConversationId = await createGroupChatConversation(currentClientId);
                    $this.attr('data-id', newConversationId);

                    mainChatContext = {
                        id: parseInt(newConversationId, 10),
                        name: $this.data('name'),
                        route: route,
                        type: $this.data('type')
                    };
                } catch (error) {
                    console.error("Failed to create group chat:", error);
                    alert("Failed to create group chat. Please try again.");
                    return;
                }
            } else {
                const numericId = parseInt(conversationId, 10);

                if (isNaN(numericId)) {
                    console.error("Invalid conversation ID:", conversationId);
                    alert("Invalid conversation ID. Please try again.");
                    return;
                }

                mainChatContext = {
                    id: numericId,
                    name: $this.data('name'),
                    route: route,
                    type: $this.data('type')
                };
            }

            console.log("ADMIN: Switched to conversation:", mainChatContext);

            $('.admin-conversation-item.active').removeClass('active');
            $this.addClass('active').removeClass('has-unread');

            updateAdminChatHeader(mainChatContext);

            $('#message-input, #send-button').prop('disabled', false);

            try {
                await connection.invoke("JoinPrivateChat", mainChatContext.id.toString());
                console.log(`ADMIN: Successfully joined SignalR group for conversation ${mainChatContext.id}`);
            } catch (error) {
                console.error(`ADMIN: Failed to join SignalR group for conversation ${mainChatContext.id}:`, error);
            }

            await loadAdminChatMessages(mainChatContext.id);
        });

        $(floatingChatContainer).on('click', e => {
            const chatBox = e.target.closest('.floating-chat-box');
            if (!chatBox) return;

            if (e.target.closest('.action-close')) {
                chatBox.remove();
                return;
            }

            if (e.target.closest('.action-minimize') || e.target.closest('.chat-box-header')) {
                chatBox.classList.toggle('collapsed');
                return;
            }

            if (e.target.closest('.action-send')) {
                const textarea = chatBox.querySelector('textarea');
                const text = textarea.value.trim();
                if (!text) return;

                const payload = {
                    Instruction: text,
                    InstructionId: parseInt(chatBox.dataset.id, 10),
                    ClientId: currentClientId,
                    InsertUser: currentUser.id,
                    Remarks: "Message from admin panel (floating)"
                };

                fetch(`/v1/api/instructions/reply`, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify(payload)
                })
                    .then(res => res.json())
                    .then(savedMessage => {
                        const container = chatBox.querySelector('.chat-box-body');
                        const senderName = currentUser.name || 'Admin';
                        const msgRow = document.createElement('div');
                        msgRow.className = 'message-row sent';
                        msgRow.innerHTML = `<div class="message-content"><div class="message-bubble"><p class="message-text">${escapeHtml(text)}</p></div><span class="message-timestamp">${senderName} - ${formatTimestamp(new Date())}</span></div>`;
                        container.appendChild(msgRow);
                        scrollToBottom(container);

                        connection.invoke("SendAdminMessage", savedMessage);
                    })
                    .catch(err => {
                        console.error('Error sending floating chat message:', err);
                        alert('Failed to send message. Please try again.');
                    });

                textarea.value = '';
            }
        });

        $(mainSendButton).on('click', sendMainChatMessage);
        $(mainMessageInput).on('keyup', e => {
            if (e.key === 'Enter' && !e.shiftKey) {
                e.preventDefault();
                sendMainChatMessage();
            }
        });

        $('#logout-button').on('click', function (e) {
            e.preventDefault();
            window.location.href = '/Login/Logout';
        });

        $('#view-all-tickets-link').on('click', function (e) {
            e.preventDefault();
            navigateToTicketManagement();
        });

        $(document).on('click', '.clickable-card', function () {
            const action = $(this).data('action');

            switch (action) {
                case 'view-all-tickets':
                    navigateToTicketManagement();
                    break;
                case 'view-all-inquiries':
                    navigateToInquiryManagement();
                    break;
                case 'view-solved-tickets':
                    navigateToTicketManagement('Resolved');
                    break;
                case 'view-solved-inquiries':
                    navigateToInquiryManagement('Completed');
                    break;
                case 'view-unsolved-tickets':
                    navigateToTicketManagement('Open');
                    break;
                case 'view-unsolved-inquiries':
                    navigateToInquiryManagement('Pending');
                    break;
            }
        });

        $(document).on('click', '#btn-update-ticket', function (e) {
            e.preventDefault();
            console.log('Update ticket button clicked');
            console.log('Current ticket data:', currentTicketData);

            if (currentTicketData) {
                updateTicketStatus();
            } else {
                console.error('No current ticket data available');
                showNotification('No ticket data available. Please select a ticket first.', 'error');
            }
        });

        $(document).on('click', '#btn-update-inquiry', function () {
            if (currentInquiryData) {
                updateInquiryStatus();
            }
        });

        $(document).on('click', '#btn-close-ticket-detail', function () {
            closeTicketDetail();
        });

        $(document).on('click', '#btn-close-inquiry-detail', function () {
            closeInquiryDetail();
        });

        $(document).on('click', '#btn-start-ticket-chat', function () {
            if (currentTicketData) {
                openEnhancedFloatingChatBox(currentTicketData, 'tkt');
            }
        });

        $(document).on('click', '#btn-start-inquiry-chat', function () {
            if (currentInquiryData) {
                const route = `inquiry/${currentInquiryData.topic.toLowerCase().replace(/\s+/g, '-')}`;
                $('[data-page="chats"]').click();
                setTimeout(() => {
                    $(`.admin-conversation-item[data-id="${currentInquiryData.id}"]`).click();
                }, 500);
            }
        });

        // 🔧 ADD: Enhanced event handlers
        $(document).on('click', '#refresh-dashboard-btn', function() {
            $(this).html('<i class="fas fa-spinner fa-spin me-1"></i>Refreshing...');
            loadEnhancedDashboardData().finally(() => {
                $(this).html('<i class="fas fa-sync-alt me-1"></i>Refresh');
            });
        });

        $(document).on('click', '#view-urgent-tickets-btn', function() {
            navigateToTicketManagement('Open');
        });

        $(document).on('click', '#view-urgent-inquiries-btn', function() {
            navigateToInquiryManagement('Pending');
        });

        $(document).on('click', '#view-all-unsolved-tickets-link', function(e) {
            e.preventDefault();
            navigateToTicketManagement('Open');
        });

        $(document).on('click', '#view-all-unsolved-inquiries-link', function(e) {
            e.preventDefault();
            navigateToInquiryManagement('Pending');
        });

        console.log("ADMIN: All event handlers wired successfully");
    }

    function setupSignalRListeners() {
        connection.on("ReceivePrivateMessage", (message) => {
            console.log("ADMIN RECEIVER: Hub broadcast received. Message object:", message);
            console.log("ADMIN RECEIVER: Current user ID:", currentUser.id);
            console.log("ADMIN RECEIVER: Message insertUser:", message.insertUser);
            console.log("ADMIN RECEIVER: Message clientAuthUserId:", message.clientAuthUserId);
            console.log("ADMIN RECEIVER: Current mainChatContext.id:", mainChatContext.id);

            if (message.insertUser === currentUser.id || message.clientAuthUserId === currentUser.id) {
                console.log("ADMIN RECEIVER: Ignoring own message broadcast.");
                return;
            }

            const conversationId = message.instructionId;
            if (!conversationId) {
                console.error("ADMIN RECEIVER: Received message with no instructionId!", message);
                return;
            }

            console.log(`ADMIN RECEIVER: Processing message for conversation ${conversationId}`);

            const floatingChat = document.getElementById(`chatbox-tkt-${conversationId}`) ||
                document.getElementById(`chatbox-inq-${conversationId}`);
            if (floatingChat && !floatingChat.classList.contains('collapsed')) {
                console.log(`ADMIN RECEIVER: Message for open floating chat #${conversationId}. Appending message.`);
                const container = floatingChat.querySelector('.chat-box-body');
                const senderName = message.senderName || 'Client';

                const msgRow = document.createElement('div');
                msgRow.className = `message-row received`;
                msgRow.innerHTML = `<div class="message-content"><div class="message-bubble"><p class="message-text">${escapeHtml(message.instruction)}</p></div><span class="message-timestamp">${escapeHtml(senderName)} - ${formatTimestamp(message.dateTime)}</span></div>`;

                container.appendChild(msgRow);
                scrollToBottom(container);
                return;
            }

            if (String(mainChatContext.id) === String(conversationId)) {
                console.log(`ADMIN RECEIVER: Message for open main chat #${conversationId}. Calling displayMainChatMessage.`);
                displayMainChatMessage(message);
            } else {
                console.log(`ADMIN RECEIVER: Message for different conversation. mainChatContext.id=${mainChatContext.id}, message conversationId=${conversationId}`);
            }

            const convItem = document.querySelector(`.conversation-item[data-id="${conversationId}"]`) ||
                document.querySelector(`.admin-conversation-item[data-id="${conversationId}"]`);
            if (convItem) {
                convItem.classList.add('has-unread');
                const subtitle = convItem.querySelector('.text-muted, .admin-conversation-subtitle');
                if (subtitle) {
                    subtitle.textContent = message.instruction;
                }
            }

            if (!$('#chats-page').hasClass('active')) {
                console.log("ADMIN RECEIVER: New client message received while not on chats page");

                const chatsNavLink = document.querySelector('[data-page="chats"]');
                if (chatsNavLink && !chatsNavLink.classList.contains('has-notification')) {
                    chatsNavLink.classList.add('has-notification');
                    const badge = document.createElement('span');
                    badge.className = 'badge bg-danger ms-2 notification-badge';
                    badge.textContent = '!';
                    chatsNavLink.appendChild(badge);
                }

                if (Notification.permission === "granted") {
                    new Notification("New Message", {
                        body: `${message.senderName}: ${message.instruction}`,
                        icon: "/images/notification-icon.png"
                    });
                }
            }

            loadNotifications();
        });

        connection.on("NewTicket", (ticket) => {
            if (String(ticket.clientId) === String(currentClientId)) {
                if ($('#dashboard-page').hasClass('active')) { pageInitializers.dashboard(); }
                if (ticketsTable) { ticketsTable.ajax.reload(null, false); }
            }

            loadNotifications();
        });

        connection.on("ReceiveNotification", (notification) => {
            console.log("ADMIN: Received notification", notification);

            if (Notification.permission === "granted") {
                new Notification(notification.title, {
                    body: notification.message,
                    icon: "/images/notification-icon.png"
                });
            }

            loadNotifications();

            showNotification(notification.message, 'info');
        });
    }

    // Add this function to debug notification issues
    function debugNotificationElements() {
        console.log('=== NOTIFICATION DEBUG ===');
        console.log('Dashboard notification btn:', document.getElementById('admin-notification-btn'));
        console.log('Chats notification btn:', document.getElementById('admin-notification-btn-chats'));
        console.log('Tickets notification btn:', document.getElementById('admin-notification-btn-tickets'));
        console.log('Inquiries notification btn:', document.getElementById('admin-notification-btn-inquiries'));
        console.log('Static notification menu:', document.getElementById('admin-notification-menu'));
        console.log('Dynamic notification menu:', document.getElementById('dynamic-notification-menu'));
        
        // Check for any notification menus
        const allMenus = document.querySelectorAll('.header-notification-dropdown-menu');
        console.log('All notification menus found:', allMenus.length);
        allMenus.forEach((menu, index) => {
            console.log(`Menu ${index}:`, {
                id: menu.id,
                classes: menu.className,
                display: window.getComputedStyle(menu).display,
                visibility: window.getComputedStyle(menu).visibility,
                dimensions: menu.getBoundingClientRect()
            });
        });
        
        console.log('Current active page:', document.querySelector('.admin-page.active')?.id);
        console.log('========================');
    }

    async function init() {
        try {
            await connection.start();
            if ('Notification' in window && Notification.permission === 'default') {
                await Notification.requestPermission();
            }

            console.log("ADMIN: SignalR connection state:", connection.state);

            try {
                await connection.invoke("JoinPrivateChat", "test-group");
                console.log("ADMIN: SignalR test group join successful");
            } catch (error) {
                console.error("ADMIN: SignalR test group join failed:", error);
            }

            setupSignalRListeners();

            const meResp = await fetch('/v1/api/accounts/me');
            if (meResp.ok) {
                currentUser = await meResp.json();
                console.log("ADMIN: Current user loaded:", currentUser);
            }
            $('#admin-username-display').text(currentUser.name);

            const clientsResp = await fetch('/v1/api/clients');
            if (clientsResp.ok) {
                const clients = await clientsResp.json();
                let optionsHtml = '<option value="">All Clients</option>';
                optionsHtml += clients.map(c => `<option value="${c.id}">${escapeHtml(c.name)}</option>`).join('');

                $('.client-switcher').html(optionsHtml);

                currentClientId = "";
                $('.client-switcher').val("");

                wireEvents();

                initializeNotifications();

                setTimeout(debugNotificationElements, 1000);

                if (pageInitializers.dashboard) {
                    await pageInitializers.dashboard();
                }
            }

            console.log("ADMIN: Initialization completed successfully");

        } catch (err) {
            console.error("Initialization failed:", err);
            $('body').html('<div class="alert alert-danger m-5"><strong>Error:</strong> Could not initialize admin panel. Please check the connection and API status.</div>');
        }
    }

    init();
});