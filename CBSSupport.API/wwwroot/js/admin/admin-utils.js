/**
 * Admin Panel Utility Functions
 * Shared helper functions used across all admin modules
 */
"use strict";

window.AdminUtils = (() => {

    // ============================================
    // 🔧 UTILITY FUNCTIONS
    // ============================================

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

    // ============================================
    // 🎯 API HELPERS
    // ============================================

    async function apiRequest(url, options = {}) {
        try {
            const response = await fetch(url, {
                headers: {
                    'Content-Type': 'application/json',
                    ...options.headers
                },
                ...options
            });

            if (!response.ok) {
                const errorData = await response.json().catch(() => ({ message: 'Unknown error' }));
                throw new Error(errorData.message || `HTTP ${response.status}: ${response.statusText}`);
            }

            return await response.json();
        } catch (error) {
            console.error(`API Request Error (${url}):`, error);
            throw error;
        }
    }

    // ============================================
    // 🎨 DOM HELPERS
    // ============================================

    function createElement(tag, className = '', innerHTML = '') {
        const element = document.createElement(tag);
        if (className) element.className = className;
        if (innerHTML) element.innerHTML = innerHTML;
        return element;
    }

    function getElement(selector) {
        return document.querySelector(selector);
    }

    function getAllElements(selector) {
        return document.querySelectorAll(selector);
    }

    // ============================================
    // 📝 VALIDATION HELPERS
    // ============================================

    function validateEmail(email) {
        const re = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;
        return re.test(email);
    }

    function validateRequired(value) {
        return value && value.trim().length > 0;
    }

    // ============================================
    // 🔄 DEBOUNCE & THROTTLE
    // ============================================

    function debounce(func, wait) {
        let timeout;
        return function executedFunction(...args) {
            const later = () => {
                clearTimeout(timeout);
                func(...args);
            };
            clearTimeout(timeout);
            timeout = setTimeout(later, wait);
        };
    }

    function throttle(func, limit) {
        let inThrottle;
        return function () {
            const args = arguments;
            const context = this;
            if (!inThrottle) {
                func.apply(context, args);
                inThrottle = true;
                setTimeout(() => inThrottle = false, limit);
            }
        };
    }

    // ============================================
    // 🔗 PUBLIC API
    // ============================================

    return {
        // Formatting functions
        formatTimestamp,
        escapeHtml,
        scrollToBottom,
        formatDateForSeparator,
        generatePriorityBadge,
        generateStatusBadge,
        getTimeAgo,

        // UI functions
        showNotification,

        // API functions
        apiRequest,

        // DOM functions
        createElement,
        getElement,
        getAllElements,

        // Validation functions
        validateEmail,
        validateRequired,

        // Utility functions
        debounce,
        throttle
    };
})();