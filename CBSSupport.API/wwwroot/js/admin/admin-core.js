"use strict";

window.AdminCore = (() => {

    let currentUser = { name: "Admin", id: null };
    let currentClientId = null;
    let connection = null;

    const pageInitializers = {
        dashboard: async function () {
            console.log("🎯 Loading Enhanced Dashboard...");
            try {
                if (window.AdminDashboard) {
                    await window.AdminDashboard.loadEnhancedDashboardData(currentClientId);
                }
            } catch (error) {
                console.error("Dashboard initialization error:", error);
                AdminUtils.showNotification('Failed to initialize dashboard', 'error');
            }
        },

        chats: async function () {
            console.log("💬 Initializing chats page for client:", currentClientId);

            if (window.AdminChat) {
                await window.AdminChat.initializeChatsPage(currentClientId);
            }
        },

        'ticket-management': function () {
            console.log("🎫 Initializing ticket management...");
            if (window.AdminTickets) {
                window.AdminTickets.initialize();
            }
        },

        'inquiry-management': function () {
            console.log("❓ Initializing inquiry management...");
            if (window.AdminInquiries) {
                window.AdminInquiries.initialize();
            }
        }
    };

    function handleClientChange() {
        $('.client-switcher').on('change', function () {
            const selectedClientId = $(this).val();
            currentClientId = selectedClientId;

            $('.client-switcher').val(selectedClientId);

            const activePage = $('.admin-sidebar .nav-link.active').data('page');

            if (activePage === 'dashboard' || activePage === 'chats') {
                if (pageInitializers[activePage]) {
                    pageInitializers[activePage]();
                }
            }

            if (window.AdminTickets && window.AdminTickets.getTicketsTable) {
                const ticketsTable = window.AdminTickets.getTicketsTable();
                if (ticketsTable) {
                    const clientName = $(this).find('option:selected').text();
                    const searchTerm = selectedClientId ? `^${clientName}$` : '';
                    ticketsTable.column(1).search(searchTerm, true, false).draw();
                }
            }

            if (window.AdminInquiries && window.AdminInquiries.getInquiriesTable) {
                const inquiriesTable = window.AdminInquiries.getInquiriesTable();
                if (inquiriesTable) {
                    const clientName = $(this).find('option:selected').text();
                    const searchTerm = selectedClientId ? `^${clientName}$` : '';
                    inquiriesTable.column(1).search(searchTerm, true, false).draw();
                }
            }
        });
    }

    function initializeEnhancedEventHandlers() {
        $(document).on('click', '#refresh-dashboard-btn', function () {
            $(this).html('<i class="fas fa-spinner fa-spin me-1"></i>Refreshing...');
            if (window.AdminDashboard) {
                window.AdminDashboard.loadEnhancedDashboardData(currentClientId).finally(() => {
                    $(this).html('<i class="fas fa-sync-alt me-1"></i>Refresh');
                });
            }
        });

        $(document).on('click', '#btn-update-ticket', function (e) {
            e.preventDefault();
            if (window.AdminTickets && window.AdminTickets.updateTicketStatus) {
                window.AdminTickets.updateTicketStatus();
            }
        });

        $(document).on('click', '#btn-update-inquiry', function () {
            if (window.AdminInquiries && window.AdminInquiries.updateInquiryStatus) {
                window.AdminInquiries.updateInquiryStatus();
            }
        });

        $(document).on('click', '#btn-close-ticket-detail', function () {
            if (window.AdminTickets && window.AdminTickets.closeTicketDetail) {
                window.AdminTickets.closeTicketDetail();
            }
        });

        $(document).on('click', '#btn-close-inquiry-detail', function () {
            if (window.AdminInquiries && window.AdminInquiries.closeInquiryDetail) {
                window.AdminInquiries.closeInquiryDetail();
            }
        });

        $(document).on('click', '#btn-start-ticket-chat', function () {
            if (window.AdminTickets && window.AdminTickets.startTicketChat) {
                window.AdminTickets.startTicketChat();
            }
        });

        $(document).on('click', '#btn-start-inquiry-chat', function () {
            if (window.AdminInquiries && window.AdminInquiries.startInquiryChat) {
                window.AdminInquiries.startInquiryChat();
            }
        });

        console.log("AdminCore: Enhanced event handlers initialized");
    }

    async function initialize() {
        console.log("🚀 AdminCore: Starting initialization...");

        try {
            if (window.AdminSignalR) {
                connection = await window.AdminSignalR.initialize();
                console.log("✅ SignalR connection established");
            }

            try {
                const meResp = await fetch('/v1/api/accounts/me');
                if (meResp.ok) {
                    currentUser = await meResp.json();
                    console.log("✅ Current user loaded:", currentUser);
                    $('#admin-username-display').text(currentUser.name);
                }
            } catch (error) {
                console.warn("⚠️ Could not load user info:", error);
            }

            try {
                const clientsResp = await fetch('/v1/api/clients');
                if (clientsResp.ok) {
                    const clients = await clientsResp.json();
                    let optionsHtml = '<option value="">All Clients</option>';
                    optionsHtml += clients.map(c => `<option value="${c.id}">${AdminUtils.escapeHtml(c.name)}</option>`).join('');

                    $('.client-switcher').html(optionsHtml);
                    currentClientId = "";
                    $('.client-switcher').val("");

                    console.log("✅ Client switchers populated");
                }
            } catch (error) {
                console.error("❌ Error loading clients:", error);
            }

            if (window.AdminNavigation) {
                window.AdminNavigation.initializeNavigationEvents();
                console.log("✅ Navigation module initialized");
            }

            if (window.AdminNotifications) {
                window.AdminNotifications.initialize();
                console.log("✅ Notifications module initialized");
            }

            if (window.AdminChat) {
                window.AdminChat.initializeChatSidebar();
                console.log("✅ Chat module initialized");
            }

            handleClientChange();

            initializeEnhancedEventHandlers();

            if (pageInitializers.dashboard) {
                await pageInitializers.dashboard();
                console.log("✅ Dashboard initialized");
            }

            if ('Notification' in window && Notification.permission === 'default') {
                await Notification.requestPermission();
            }

            console.log("🎉 AdminCore: Initialization completed successfully!");

        } catch (error) {
            console.error("❌ AdminCore: Initialization failed:", error);
            AdminUtils.showNotification('Failed to initialize admin panel. Please refresh the page.', 'error');
        }
    }

    return {
        initialize,
        getCurrentUser: () => currentUser,
        getCurrentClientId: () => currentClientId,
        setCurrentClientId: (clientId) => { currentClientId = clientId; },
        getConnection: () => connection,
        getPageInitializers: () => pageInitializers
    };
})();

document.addEventListener('DOMContentLoaded', () => {
    window.AdminCore.initialize();
});