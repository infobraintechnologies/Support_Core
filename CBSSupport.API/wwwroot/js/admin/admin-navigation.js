/**
 * Admin Panel Navigation System
 * Handles page routing and navigation between different admin sections
 */
"use strict";

window.AdminNavigation = (() => {

    // ============================================
    // 🧭 PAGE NAVIGATION
    // ============================================

    function setActivePageLink(pageName) {
        const links = $('.admin-sidebar .nav-link');
        links.removeClass('active').removeAttr('aria-current');
        links.filter(`[data-page="${pageName}"]`)
            .addClass('active')
            .attr('aria-current', 'page');
    }

    function navigateToTicketManagement(statusFilter = null) {
        setActivePageLink('ticket-management');
        $('.admin-page.active').removeClass('active');
        $('#ticket-management-page').addClass('active');

        if (window.AdminCore && window.AdminCore.getPageInitializers) {
            const pageInitializers = window.AdminCore.getPageInitializers();
            if (pageInitializers['ticket-management']) {
                pageInitializers['ticket-management']();
            }
        }

        if (window.AdminTickets?.filterByStatus) {
            window.AdminTickets.filterByStatus(statusFilter);
        }
    }

    function navigateToInquiryManagement(statusFilter = null) {
        setActivePageLink('inquiry-management');
        $('.admin-page.active').removeClass('active');
        $('#inquiry-management-page').addClass('active');

        if (window.AdminCore && window.AdminCore.getPageInitializers) {
            const pageInitializers = window.AdminCore.getPageInitializers();
            if (pageInitializers['inquiry-management']) {
                pageInitializers['inquiry-management']();
            }
        }

        if (window.AdminInquiries?.filterByStatus) {
            window.AdminInquiries.filterByStatus(statusFilter);
        }
    }

    function navigateToChatsPage() {
        setActivePageLink('chats');
        $('.admin-page.active').removeClass('active');
        $('#chats-page').addClass('active');

        if (window.AdminCore && window.AdminCore.getPageInitializers) {
            const pageInitializers = window.AdminCore.getPageInitializers();
            if (pageInitializers['chats']) {
                pageInitializers['chats']();
            }
        }
    }

    function navigateToDashboard() {
        setActivePageLink('dashboard');
        $('.admin-page.active').removeClass('active');
        $('#dashboard-page').addClass('active');

        if (window.AdminCore && window.AdminCore.getPageInitializers) {
            const pageInitializers = window.AdminCore.getPageInitializers();
            if (pageInitializers['dashboard']) {
                pageInitializers['dashboard']();
            }
        }
    }

    // ============================================
    // 🎯 ACTION HANDLERS
    // ============================================

    function handleCardAction(action) {
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
            default:
                console.warn(`Unknown action: ${action}`);
        }
    }

    // ============================================
    // 🔗 EVENT HANDLERS
    // ============================================

    function initializeNavigationEvents() {
        // Sidebar navigation
        $('.admin-sidebar .nav-link').on('click', function (e) {
            e.preventDefault();
            const pageName = $(this).data('page');

            setActivePageLink(pageName);
            $('.admin-page.active').removeClass('active');
            $('#' + pageName + '-page').addClass('active');

            // Clear notification badge when navigating to chats
            if (pageName === 'chats') {
                const chatsNavLink = document.querySelector('[data-page="chats"]');
                if (chatsNavLink) {
                    chatsNavLink.classList.remove('has-notification');
                    const badge = chatsNavLink.querySelector('.notification-badge');
                    if (badge) badge.remove();
                }
            }

            // Initialize page if initializer exists
            if (window.AdminCore && window.AdminCore.getPageInitializers) {
                const pageInitializers = window.AdminCore.getPageInitializers();
                if (pageInitializers[pageName]) {
                    pageInitializers[pageName]();
                }
            }
        });

        // Clickable card actions
        $(document).on('click', '.clickable-card', function () {
            const action = $(this).data('action');
            if (action) {
                handleCardAction(action);
            }
        });

        // Quick navigation links
        $('#view-all-tickets-link').on('click', function (e) {
            e.preventDefault();
            navigateToTicketManagement();
        });

        $('#view-urgent-tickets-btn').on('click', function () {
            navigateToTicketManagement('Open');
        });

        $('#view-urgent-inquiries-btn').on('click', function () {
            navigateToInquiryManagement('Pending');
        });

        $('#view-all-unsolved-tickets-link').on('click', function (e) {
            e.preventDefault();
            navigateToTicketManagement('Open');
        });

        $('#view-all-unsolved-inquiries-link').on('click', function (e) {
            e.preventDefault();
            navigateToInquiryManagement('Pending');
        });

        console.log("AdminNavigation: Event handlers initialized");
    }

    // ============================================
    // 🔗 PUBLIC API
    // ============================================

    return {
        navigateToTicketManagement,
        navigateToInquiryManagement,
        navigateToChatsPage,
        navigateToDashboard,
        handleCardAction,
        initializeNavigationEvents
    };
})();
