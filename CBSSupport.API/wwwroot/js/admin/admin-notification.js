/**
 * Admin Panel Notifications System
 * Handles all notification functionality including loading, rendering, and user interactions
 */
"use strict";

window.AdminNotifications = (() => {

    // ============================================
    // 🔔 STATE MANAGEMENT
    // ============================================

    let unreadNotificationCount = 0;
    let notificationPollingInterval = null;

    // ============================================
    // 🔔 NOTIFICATION PROCESSING
    // ============================================

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

            notification.timeAgo = AdminUtils.getTimeAgo(notification.createdAt);
            notification.icon = getNotificationIcon(notification.type);
            notifications.push(notification);
        });

        // Add cached read notifications
        cachedReadNotifications.forEach(cachedNotification => {
            if (!notifications.find(n => n.id === cachedNotification.id)) {
                cachedNotification.isRead = true;
                cachedNotification.timeAgo = AdminUtils.getTimeAgo(cachedNotification.createdAt);
                notifications.push(cachedNotification);
            }
        });

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
        let container = document.getElementById('admin-notification-list') ||
            document.getElementById('dynamic-notification-list');

        if (!container) {
            console.warn('⚠️ No notification container found');
            return;
        }

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
                        <div class="notification-title">${AdminUtils.escapeHtml(notification.title)}</div>
                        <div class="notification-message">${AdminUtils.escapeHtml(notification.message)}</div>
                        <div class="notification-time">${notification.timeAgo}</div>
                    </div>
                </div>
            </div>
        `).join('');
    }

    // ============================================
    // 🔔 NOTIFICATION ACTIONS
    // ============================================

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
                const updatedReadNotifications = [...currentNotifications, ...existingReadNotifications].slice(0, 20);

                localStorage.setItem('admin_read_notifications', JSON.stringify(updatedReadNotifications));

                document.querySelectorAll('.notification-item.unread').forEach(item => {
                    item.classList.remove('unread');
                });

                updateNotificationBadge(0);
                AdminUtils.showNotification('All notifications marked as read', 'success');
            }
        } catch (error) {
            console.error('Error marking all notifications as read:', error);
            AdminUtils.showNotification('Failed to mark notifications as read', 'error');
        }
    }

    // ============================================
    // 🔔 NOTIFICATION MENU MANAGEMENT
    // ============================================

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

        setupNotificationMenuEvents(menu);
        return menu;
    }

    function setupNotificationMenuEvents(menu) {
        const markAllBtn = menu.querySelector('.mark-all-read-btn');
        if (markAllBtn) {
            markAllBtn.addEventListener('click', markAllNotificationsAsRead);
        }

        menu.addEventListener('click', async (e) => {
            const notificationItem = e.target.closest('.notification-item');
            if (notificationItem) {
                const notificationId = notificationItem.dataset.id;
                const entityId = notificationItem.dataset.entityId;
                const entityType = notificationItem.dataset.entityType;

                if (notificationItem.classList.contains('unread')) {
                    await markNotificationAsRead(notificationId);
                }

                // Navigate to relevant page
                if (entityType === 'message') {
                    AdminNavigation.navigateToChatsPage();
                    setTimeout(async () => {
                        if (window.AdminChat && window.AdminChat.openChatConversation) {
                            await window.AdminChat.openChatConversation(entityId);
                        }
                    }, 500);
                } else if (entityType === 'ticket' && entityId) {
                    AdminNavigation.navigateToTicketManagement();
                    setTimeout(() => {
                        if (window.AdminTickets && window.AdminTickets.loadTicketDetails) {
                            window.AdminTickets.loadTicketDetails(entityId);
                        }
                    }, 500);
                } else if (entityType === 'inquiry' && entityId) {
                    AdminNavigation.navigateToInquiryManagement();
                    setTimeout(() => {
                        if (window.AdminInquiries && window.AdminInquiries.loadInquiryDetails) {
                            window.AdminInquiries.loadInquiryDetails(entityId);
                        }
                    }, 500);
                }

                menu.remove();
            }
        });
    }

    // ============================================
    // 🔔 INITIALIZATION
    // ============================================

    function initialize() {
        const buttonIds = [
            'admin-notification-btn',
            'admin-notification-btn-chats',
            'admin-notification-btn-tickets',
            'admin-notification-btn-inquiries'
        ];

        buttonIds.forEach(btnId => {
            const btn = document.getElementById(btnId);

            if (btn) {
                btn.replaceWith(btn.cloneNode(true));
                const newBtn = document.getElementById(btnId);

                newBtn.addEventListener('click', async (e) => {
                    e.preventDefault();
                    e.stopPropagation();

                    document.querySelectorAll('.header-notification-dropdown-menu').forEach(menu => {
                        menu.remove();
                    });

                    const notificationMenu = createNotificationMenu();

                    if (btnId === 'admin-notification-btn') {
                        const container = newBtn.closest('.header-notification-container');
                        container.appendChild(notificationMenu);
                    } else {
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
                    } catch (error) {
                        console.error('❌ Error loading notifications:', error);
                    }
                });
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

        // Load initial notifications and start polling
        loadNotifications();

        if (notificationPollingInterval) {
            clearInterval(notificationPollingInterval);
        }
        notificationPollingInterval = setInterval(loadNotifications, 30000);
    }

    // ============================================
    // 🔗 PUBLIC API
    // ============================================

    return {
        initialize,
        loadNotifications,
        markNotificationAsRead,
        markAllNotificationsAsRead,
        updateNotificationBadge
    };
})();