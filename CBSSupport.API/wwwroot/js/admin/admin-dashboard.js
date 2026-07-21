/**
 * Admin Panel Dashboard System
 * Handles dashboard statistics, charts, and data visualization
 */
"use strict";

window.AdminDashboard = (() => {

    // ============================================
    // 📊 STATE MANAGEMENT
    // ============================================

    let ticketPriorityChart = null;

    // ============================================
    // 📊 DASHBOARD DATA LOADING
    // ============================================

    async function loadEnhancedDashboardData(currentClientId) {
        try {
            if (!currentClientId) {
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

                updateBasicStats(stats, ticketData, inquiryData);
                updateUrgentSections(ticketData, inquiryData);
                await loadUnsolvedItems(ticketData, inquiryData);
                updatePriorityChart(ticketData, currentClientId);
                loadRecentActivity(ticketData);

            } else {
                await loadClientSpecificDashboard(currentClientId);
            }
        } catch (error) {
            console.error('Error loading enhanced dashboard data:', error);
            AdminUtils.showNotification('Failed to load dashboard data', 'error');
        }
    }

    async function loadClientSpecificDashboard(currentClientId) {
        try {
            const [ticketsRes, inquiriesRes] = await Promise.all([
                fetch(`/v1/api/instructions/tickets/${currentClientId}`),
                fetch(`/v1/api/instructions/inquiries/${currentClientId}`)
            ]);

            if (!ticketsRes.ok || !inquiriesRes.ok) {
                throw new Error("Failed to fetch client-specific dashboard data");
            }

            const tickets = await ticketsRes.json();
            const inquiries = await inquiriesRes.json();
            const ticketData = tickets.data || [];
            const inquiryData = inquiries.data || [];

            const stats = {
                totalTickets: ticketData.length,
                openTickets: ticketData.filter(t => t.status !== 'Resolved').length,
                resolvedTickets: ticketData.filter(t => t.status === 'Resolved').length,
                totalInquiries: inquiryData.length
            };

            updateBasicStats(stats, ticketData, inquiryData);
            updateUrgentSections(ticketData, inquiryData);
            await loadUnsolvedItems(ticketData, inquiryData);
            updatePriorityChart(ticketData, currentClientId);
            loadRecentActivity(ticketData);

        } catch (error) {
            console.error('Error loading client-specific dashboard:', error);
            AdminUtils.showNotification('Failed to load client dashboard data', 'error');
        }
    }

    // ============================================
    // 📊 STATISTICS UPDATES
    // ============================================

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

        // Show/hide urgent alert section
        const alertSection = $('#urgent-alert-section');
        if (totalUnsolved > 0) {
            alertSection.show();

            if (totalUnsolved > 10) {
                alertSection.addClass('high-priority');
            } else {
                alertSection.removeClass('high-priority');
            }
        } else {
            alertSection.hide();
        }

        // Update badge colors based on count
        const ticketsBadge = $('#urgent-tickets-badge');
        const inquiriesBadge = $('#urgent-inquiries-badge');

        if (unsolvedTickets === 0) {
            ticketsBadge.removeClass('bg-danger').addClass('bg-success').text('CLEAR');
        } else {
            ticketsBadge.removeClass('bg-success').addClass('bg-danger').text('URGENT');
        }

        if (unsolvedInquiries === 0) {
            inquiriesBadge.removeClass('bg-warning').addClass('bg-success').text('CLEAR');
        } else {
            inquiriesBadge.removeClass('bg-success').addClass('bg-warning').text('PENDING');
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
            const timeAgo = AdminUtils.getTimeAgo(ticket.date);

            return `
                <div class="unsolved-ticket-item" onclick="AdminDashboard.navigateToTicketDetails(${ticket.id})">
                    <div class="unsolved-item-header">
                        <div>
                            <div class="unsolved-item-title">#${ticket.id} - ${AdminUtils.escapeHtml(ticket.subject || 'General Support')}</div>
                            <div class="unsolved-item-meta">
                                <strong>${AdminUtils.escapeHtml(ticket.clientName || 'Unknown Client')}</strong> • 
                                Created by ${AdminUtils.escapeHtml(ticket.createdBy || 'Unknown')}
                            </div>
                        </div>
                        <div class="unsolved-item-priority">
                            ${AdminUtils.generatePriorityBadge(ticket.priority)}
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
            const timeAgo = AdminUtils.getTimeAgo(inquiry.date);

            return `
                <div class="unsolved-inquiry-item" onclick="AdminDashboard.navigateToInquiryDetails(${inquiry.id})">
                    <div class="unsolved-item-header">
                        <div>
                            <div class="unsolved-item-title">#INQ-${inquiry.id} - ${AdminUtils.escapeHtml(inquiry.topic || 'General Inquiry')}</div>
                            <div class="unsolved-item-meta">
                                <strong>${AdminUtils.escapeHtml(inquiry.clientName || 'Unknown Client')}</strong> • 
                                By ${AdminUtils.escapeHtml(inquiry.inquiredBy || 'Unknown')}
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

    function loadRecentActivity(ticketData) {
        const recentTicketsList = $('#recent-tickets-list');
        recentTicketsList.empty();

        if (ticketData.length > 0) {
            const recentTickets = ticketData.slice(0, 5);
            recentTickets.forEach(ticket => {
                const lastUpdate = new Date(ticket.date).toLocaleString([], {
                    year: 'numeric',
                    month: 'numeric',
                    day: 'numeric',
                    hour: '2-digit',
                    minute: '2-digit'
                });
                const statusIcon = ticket.status === 'Resolved' ? 'fa-check-circle text-success' : 'fa-clock text-warning';

                const itemHtml = `
                    <div class="recent-ticket-item" onclick="AdminDashboard.navigateToTicketDetails(${ticket.id})" style="cursor: pointer;">
                        <div class="recent-ticket-info">
                            <div class="d-flex align-items-center mb-1">
                                <i class="fas ${statusIcon} me-2"></i>
                                <strong>#${ticket.id} - ${AdminUtils.escapeHtml(ticket.clientName)}</strong>
                            </div>
                            <small>Subject: ${AdminUtils.escapeHtml(ticket.subject)} | Last Update: ${lastUpdate}</small>
                        </div>
                        ${AdminUtils.generatePriorityBadge(ticket.priority)}
                    </div>`;
                recentTicketsList.append(itemHtml);
            });
        } else {
            recentTicketsList.html('<p class="text-muted p-3">No recent tickets found.</p>');
        }
    }

    function updatePriorityChart(ticketData, currentClientId) {
        const priorityCounts = { Low: 0, Normal: 0, High: 0, Urgent: 0 };
        let activeTickets = 0;

        ticketData.forEach(ticket => {
            if (ticket.status !== 'Resolved') {
                activeTickets++;
                const priority = ticket.priority || 'Normal';
                if (priorityCounts.hasOwnProperty(priority)) {
                    priorityCounts[priority]++;
                }
            }
        });

        const chartCanvas = document.getElementById('ticketPriorityChart');
        if (chartCanvas) {
            if (ticketPriorityChart) {
                ticketPriorityChart.destroy();
            }

            const centerTextPlugin = {
                id: 'centerText',
                beforeDraw: (chart) => {
                    const { ctx, width, height } = chart;
                    ctx.restore();
                    const text = currentClientId ? "Active tickets" : "Total active tickets";
                    const total = chart.options.plugins.centerText.total;
                    ctx.font = "bold 20px sans-serif";
                    ctx.textBaseline = 'middle';
                    ctx.textAlign = 'center';
                    const textX = Math.round(width / 2);
                    const textY = Math.round(height / 2);
                    ctx.fillText(total, textX, textY + 10);
                    ctx.font = "12px sans-serif";
                    ctx.fillStyle = '#6c757d';
                    ctx.fillText(text, textX, textY - 10);
                    ctx.save();
                }
            };

            ticketPriorityChart = new Chart(chartCanvas.getContext('2d'), {
                type: 'doughnut',
                data: {
                    labels: ['Low', 'Normal', 'High', 'Urgent'],
                    datasets: [{
                        data: [priorityCounts.Low, priorityCounts.Normal, priorityCounts.High, priorityCounts.Urgent],
                        backgroundColor: ['#28a745', '#17a2b8', '#ffc107', '#dc3545'],
                        borderWidth: 0,
                        hoverOffset: 10
                    }]
                },
                options: {
                    responsive: true,
                    maintainAspectRatio: false,
                    cutout: '70%',
                    plugins: {
                        legend: {
                            position: 'bottom',
                            labels: {
                                usePointStyle: true,
                                boxWidth: 8,
                                padding: 20,
                                font: {
                                    size: 12
                                }
                            }
                        },
                        centerText: { total: activeTickets },
                        tooltip: {
                            callbacks: {
                                label: function (context) {
                                    const label = context.label || '';
                                    const value = context.parsed;
                                    const total = context.dataset.data.reduce((a, b) => a + b, 0);
                                    const percentage = total > 0 ? Math.round((value / total) * 100) : 0;
                                    return `${label}: ${value} (${percentage}%)`;
                                }
                            }
                        }
                    },
                    animation: {
                        animateScale: true,
                        duration: 1000
                    }
                },
                plugins: [centerTextPlugin]
            });
        }
    }

    // Navigation functions for unsolved items
    function navigateToTicketDetails(ticketId) {
        if (window.AdminNavigation) {
            AdminNavigation.navigateToTicketManagement();
            setTimeout(() => {
                if (window.AdminTickets && window.AdminTickets.loadTicketDetails) {
                    window.AdminTickets.loadTicketDetails(ticketId);
                }
            }, 500);
        }
    }

    function navigateToInquiryDetails(inquiryId) {
        if (window.AdminNavigation) {
            AdminNavigation.navigateToInquiryManagement();
            setTimeout(() => {
                if (window.AdminInquiries && window.AdminInquiries.loadInquiryDetails) {
                    window.AdminInquiries.loadInquiryDetails(inquiryId);
                }
            }, 500);
        }
    }

    // ============================================
    // 🔗 PUBLIC API
    // ============================================

    return {
        loadEnhancedDashboardData,
        updateBasicStats,
        updateUrgentSections,
        updatePriorityChart,
        navigateToTicketDetails,
        navigateToInquiryDetails
    };
})();