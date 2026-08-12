"use strict";

window.AdminDashboard = (() => {
    const maximumQueueItems = 100;
    let requestVersion = 0;
    let resizeTimer = null;
    let currentChartData = null;

    function setText(id, value) {
        const element = document.getElementById(id);
        if (element) element.textContent = String(value ?? "");
    }

    async function fetchJson(url) {
        const response = await fetch(url, { credentials: "same-origin" });
        const contentType = response.headers.get("content-type") || "";
        const body = contentType.includes("json") ? await response.json() : null;
        if (!response.ok) {
            throw new Error(body?.detail || body?.title || `Request failed (${response.status}).`);
        }
        return body;
    }

    function caseListUrl(resource, clientId) {
        const query = new URLSearchParams({ pageSize: String(maximumQueueItems) });
        if (clientId) query.set("clientId", String(clientId));
        return `/api/v1/admin/${resource}?${query}`;
    }

    function pageItems(page) {
        return Array.isArray(page?.items) ? page.items : [];
    }

    function isResolved(ticket) {
        return String(ticket?.status || "").toLowerCase() === "resolved";
    }

    function isCompleted(inquiry) {
        return String(inquiry?.status || "").toLowerCase() === "completed";
    }

    function itemDate(item) {
        const value = item?.createdAt || item?.date;
        const parsed = value ? new Date(value) : null;
        return parsed && !Number.isNaN(parsed.getTime()) ? parsed : null;
    }

    function getClientName(clientId) {
        if (!clientId) return "All clients";
        const option = Array.from(
            document.getElementById("client-switcher-dashboard")?.options || [])
            .find(candidate => String(candidate.value) === String(clientId));
        return option?.textContent?.trim() || `Client ${clientId}`;
    }

    function getItemClientName(item) {
        return getClientName(item?.clientId) || "Client";
    }

    async function loadEnhancedDashboardData(currentClientId) {
        const version = ++requestVersion;
        renderLoadingState(currentClientId);

        try {
            const requests = [
                fetchJson(caseListUrl("tickets", currentClientId)),
                fetchJson(caseListUrl("inquiries", currentClientId))
            ];
            if (!currentClientId) requests.push(fetchJson("/v1/api/dashboard/stats/all"));

            const [ticketPage, inquiryPage, aggregateStats = null] = await Promise.all(requests);
            if (version !== requestVersion) return;

            const tickets = pageItems(ticketPage);
            const inquiries = pageItems(inquiryPage);
            const summary = buildSummary(
                tickets,
                inquiries,
                aggregateStats,
                Boolean(ticketPage?.nextCursor),
                Boolean(inquiryPage?.nextCursor));

            updateBasicStats(summary);
            updateScopeSummary(currentClientId, summary);
            renderWorklists(tickets, inquiries, summary);
            renderRecentActivity(tickets);
            renderCharts(summary);
        } catch (error) {
            if (version !== requestVersion) return;
            console.error("AdminDashboard: dashboard load failed.", error);
            renderDashboardError(error);
            AdminUtils.showNotification("Failed to load dashboard data.", "error");
        }
    }

    function buildSummary(tickets, inquiries, aggregateStats, ticketHasMore, inquiryHasMore) {
        const loadedOpenTickets = tickets.filter(ticket => !isResolved(ticket)).length;
        const loadedResolvedTickets = tickets.length - loadedOpenTickets;
        const pendingInquiries = inquiries.filter(inquiry => !isCompleted(inquiry)).length;
        const completedInquiries = inquiries.length - pendingInquiries;
        const priorityCounts = { Low: 0, Normal: 0, High: 0, Urgent: 0 };

        tickets.filter(ticket => !isResolved(ticket)).forEach(ticket => {
            const priority = priorityCounts[ticket.priority] == null ? "Normal" : ticket.priority;
            priorityCounts[priority] += 1;
        });

        return {
            totalTickets: Number(aggregateStats?.totalTickets ?? tickets.length),
            openTickets: Number(aggregateStats?.openTickets ?? loadedOpenTickets),
            resolvedTickets: Number(aggregateStats?.resolvedTickets ?? loadedResolvedTickets),
            totalInquiries: Number(aggregateStats?.totalInquiries ?? inquiries.length),
            pendingInquiries,
            completedInquiries,
            loadedOpenTickets,
            loadedResolvedTickets,
            priorityCounts,
            ticketHasMore,
            inquiryHasMore
        };
    }

    function updateBasicStats(summary) {
        setText("stat-total-tickets", summary.totalTickets);
        setText("stat-open-tickets", summary.openTickets);
        setText("stat-resolved-tickets", summary.resolvedTickets);
        setText("stat-total-inquiries", summary.totalInquiries);
        setText("stat-solved-inquiries", formatBoundedCount(
            summary.completedInquiries,
            summary.inquiryHasMore));
        setText("stat-unsolved-inquiries", formatBoundedCount(
            summary.pendingInquiries,
            summary.inquiryHasMore));

        const ticketRate = summary.totalTickets > 0
            ? Math.round(summary.resolvedTickets / summary.totalTickets * 100)
            : 0;
        const loadedInquiryTotal = summary.pendingInquiries + summary.completedInquiries;
        const inquiryRate = loadedInquiryTotal > 0
            ? Math.round(summary.completedInquiries / loadedInquiryTotal * 100)
            : 0;
        setText("ticket-resolution-rate", `${ticketRate}% resolved`);
        setText(
            "inquiry-completion-rate",
            `${inquiryRate}% completed${summary.inquiryHasMore ? " in loaded queue" : ""}`);
        setText("critical-tickets-badge", `${summary.openTickets} open`);
        setText(
            "pending-inquiries-badge",
            `${formatBoundedCount(summary.pendingInquiries, summary.inquiryHasMore)} pending`);
    }

    function formatBoundedCount(value, hasMore) {
        return hasMore ? `${value}+` : String(value);
    }

    function updateScopeSummary(clientId, summary) {
        const scope = getClientName(clientId);
        const queueNote = summary.ticketHasMore || summary.inquiryHasMore
            ? " Worklists and charts show the latest 100 records per case type."
            : " Worklists and charts cover the full loaded queue.";
        setText(
            "dashboard-scope-summary",
            `${scope} · ${summary.openTickets} open tickets and ${formatBoundedCount(summary.pendingInquiries, summary.inquiryHasMore)} pending inquiries.${queueNote}`);
    }

    function renderLoadingState(clientId) {
        setText("dashboard-scope-summary", `${getClientName(clientId)} · Refreshing support workload…`);
        renderListState("unsolved-tickets-list", "Loading open tickets…", true);
        renderListState("unsolved-inquiries-list", "Loading pending inquiries…", true);
        renderListState("recent-tickets-list", "Loading recent activity…", true);
    }

    function renderDashboardError(error) {
        setText("dashboard-scope-summary", "Dashboard data is temporarily unavailable.");
        const message = error?.message || "Check the connection and refresh the dashboard.";
        renderListState("unsolved-tickets-list", message, false, true);
        renderListState("unsolved-inquiries-list", message, false, true);
        renderListState("recent-tickets-list", message, false, true);
        document.getElementById("dashboard-workload-chart")?.replaceChildren();
        document.getElementById("ticket-priority-chart")?.replaceChildren();
        setText("ticket-priority-summary", "Priority data unavailable.");
    }

    function renderListState(id, message, loading = false, danger = false) {
        const container = document.getElementById(id);
        if (!container) return;
        const state = document.createElement("div");
        state.className = `dashboard-list-state${danger ? " dashboard-list-state--danger" : ""}`;
        if (loading) {
            const spinner = document.createElement("span");
            spinner.className = "spinner-border spinner-border-sm";
            spinner.setAttribute("aria-hidden", "true");
            state.appendChild(spinner);
        }
        state.appendChild(document.createTextNode(message));
        container.replaceChildren(state);
    }

    function renderWorklists(tickets, inquiries, summary) {
        const openTickets = tickets
            .filter(ticket => !isResolved(ticket))
            .sort(compareNewest)
            .slice(0, 5);
        const pendingInquiries = inquiries
            .filter(inquiry => !isCompleted(inquiry))
            .sort(compareNewest)
            .slice(0, 5);

        renderTicketWorklist(openTickets);
        renderInquiryWorklist(pendingInquiries);
        setText("critical-tickets-badge", `${summary.openTickets} open`);
    }

    function compareNewest(left, right) {
        return (itemDate(right)?.getTime() || 0) - (itemDate(left)?.getTime() || 0);
    }

    function renderTicketWorklist(tickets) {
        const container = document.getElementById("unsolved-tickets-list");
        if (!container) return;
        if (!tickets.length) {
            renderEmptyState(container, "All tickets are resolved", "There are no open tickets in this scope.");
            return;
        }
        container.replaceChildren(...tickets.map(ticket => createCaseButton({
            id: ticket.id,
            title: ticket.subject || "General support",
            client: getItemClientName(ticket),
            person: ticket.createdByName || "Unknown requester",
            date: itemDate(ticket),
            badge: ticket.priority || "Normal",
            badgeClass: priorityClass(ticket.priority),
            onClick: () => navigateToTicketDetails(ticket.id)
        })));
    }

    function renderInquiryWorklist(inquiries) {
        const container = document.getElementById("unsolved-inquiries-list");
        if (!container) return;
        if (!inquiries.length) {
            renderEmptyState(container, "All inquiries are completed", "There are no pending inquiries in this scope.");
            return;
        }
        container.replaceChildren(...inquiries.map(inquiry => createCaseButton({
            id: inquiry.id,
            prefix: "INQ-",
            title: inquiry.topic || "General inquiry",
            client: getItemClientName(inquiry),
            person: inquiry.inquiredByName || "Unknown requester",
            date: itemDate(inquiry),
            badge: "Pending",
            badgeClass: "badge-status--warning",
            onClick: () => navigateToInquiryDetails(inquiry.id)
        })));
    }

    function createCaseButton(data) {
        const button = document.createElement("button");
        button.type = "button";
        button.className = "dashboard-case-item";
        button.addEventListener("click", data.onClick);

        const copy = document.createElement("span");
        copy.className = "dashboard-case-copy";
        const title = document.createElement("span");
        title.className = "dashboard-case-title";
        title.textContent = `#${data.prefix || ""}${data.id} · ${data.title}`;
        const meta = document.createElement("span");
        meta.className = "dashboard-case-meta";
        meta.textContent = `${data.client} · ${data.person}`;
        const time = document.createElement("span");
        time.className = "dashboard-case-time";
        time.textContent = data.date ? AdminUtils.getTimeAgo(data.date.toISOString()) : "Date unavailable";
        copy.append(title, meta, time);

        const badge = document.createElement("span");
        badge.className = `badge-status ${data.badgeClass}`;
        badge.textContent = data.badge;
        button.append(copy, badge);
        return button;
    }

    function priorityClass(priority) {
        return {
            Low: "badge-status--neutral",
            Normal: "badge-status--info",
            High: "badge-status--warning",
            Urgent: "badge-status--danger"
        }[priority] || "badge-status--info";
    }

    function renderEmptyState(container, titleText, messageText) {
        const state = document.createElement("div");
        state.className = "dashboard-empty-state";
        const icon = document.createElement("i");
        icon.className = "bi bi-check-circle";
        icon.setAttribute("aria-hidden", "true");
        const title = document.createElement("strong");
        title.textContent = titleText;
        const message = document.createElement("span");
        message.textContent = messageText;
        state.append(icon, title, message);
        container.replaceChildren(state);
    }

    function renderRecentActivity(tickets) {
        const container = document.getElementById("recent-tickets-list");
        if (!container) return;
        const recent = [...tickets].sort(compareNewest).slice(0, 6);
        if (!recent.length) {
            renderListState("recent-tickets-list", "No ticket activity is available for this scope.");
            return;
        }

        container.replaceChildren(...recent.map(ticket => {
            const button = document.createElement("button");
            button.type = "button";
            button.className = "dashboard-recent-item";
            button.addEventListener("click", () => navigateToTicketDetails(ticket.id));

            const status = document.createElement("span");
            status.className = `dashboard-status-marker ${isResolved(ticket) ? "is-resolved" : "is-open"}`;
            status.textContent = isResolved(ticket) ? "Resolved" : "Open";
            const title = document.createElement("span");
            title.className = "dashboard-recent-title";
            title.textContent = `#${ticket.id} · ${ticket.subject || "General support"}`;
            const client = document.createElement("span");
            client.className = "dashboard-recent-client";
            client.textContent = getItemClientName(ticket);
            const time = document.createElement("time");
            time.className = "dashboard-recent-time";
            const date = itemDate(ticket);
            if (date) time.dateTime = date.toISOString();
            time.textContent = date ? AdminUtils.getTimeAgo(date.toISOString()) : "Date unavailable";
            button.append(status, title, client, time);
            return button;
        }));
    }

    function renderCharts(summary) {
        currentChartData = summary;
        if (!window.d3) {
            renderChartFallback();
            return;
        }
        renderWorkloadChart(summary);
        renderPriorityChart(summary.priorityCounts);
    }

    function renderChartFallback() {
        setText("dashboard-workload-summary", "Chart library unavailable. Use the summary cards for current totals.");
        setText("ticket-priority-summary", "Priority chart unavailable.");
    }

    function renderWorkloadChart(summary) {
        const data = [
            { label: "Open tickets", value: summary.loadedOpenTickets, color: "var(--color-danger)" },
            { label: "Resolved tickets", value: summary.loadedResolvedTickets, color: "var(--color-success)" },
            { label: "Pending inquiries", value: summary.pendingInquiries, color: "var(--color-warning)" },
            { label: "Completed inquiries", value: summary.completedInquiries, color: "var(--color-neutral)" }
        ];
        renderHorizontalBars("dashboard-workload-chart", data, 280);
        setText(
            "dashboard-workload-summary",
            data.map(item => `${item.label}: ${item.value}`).join("; "));
    }

    function renderPriorityChart(counts) {
        const colors = {
            Low: "var(--color-text-muted)",
            Normal: "var(--color-accent)",
            High: "var(--color-warning)",
            Urgent: "var(--color-danger)"
        };
        const data = ["Urgent", "High", "Normal", "Low"]
            .map(label => ({ label, value: counts[label], color: colors[label] }));
        renderHorizontalBars("ticket-priority-chart", data, 280);

        const legend = document.getElementById("ticket-priority-summary");
        if (!legend) return;
        legend.replaceChildren(...data.map(item => {
            const entry = document.createElement("span");
            const marker = document.createElement("span");
            marker.className = "dashboard-chart-key";
            marker.style.backgroundColor = item.color;
            marker.setAttribute("aria-hidden", "true");
            entry.append(marker, document.createTextNode(`${item.label} ${item.value}`));
            return entry;
        }));
    }

    function renderHorizontalBars(containerId, data, height) {
        const container = document.getElementById(containerId);
        if (!container) return;
        container.replaceChildren();
        const width = Math.max(container.clientWidth || 360, 280);
        const margin = { top: 12, right: 38, bottom: 24, left: 126 };
        const chartWidth = Math.max(width - margin.left - margin.right, 80);
        const chartHeight = height - margin.top - margin.bottom;
        const maximum = Math.max(window.d3.max(data, item => item.value) || 0, 1);
        const x = window.d3.scaleLinear().domain([0, maximum]).nice().range([0, chartWidth]);
        const y = window.d3.scaleBand()
            .domain(data.map(item => item.label))
            .range([0, chartHeight])
            .padding(0.36);
        const tickValues = maximum <= 5
            ? window.d3.range(0, Math.ceil(maximum) + 1)
            : x.ticks(4);

        const svg = window.d3.select(container)
            .append("svg")
            .attr("viewBox", `0 0 ${width} ${height}`)
            .attr("width", "100%")
            .attr("height", height)
            .attr("aria-hidden", "true")
            .attr("focusable", "false");
        const plot = svg.append("g").attr("transform", `translate(${margin.left},${margin.top})`);

        plot.selectAll("line.grid")
            .data(tickValues)
            .join("line")
            .attr("class", "grid")
            .attr("x1", tick => x(tick))
            .attr("x2", tick => x(tick))
            .attr("y1", 0)
            .attr("y2", chartHeight)
            .attr("stroke", "var(--color-border)");

        plot.selectAll("rect.bar")
            .data(data)
            .join("rect")
            .attr("class", "bar")
            .attr("x", 0)
            .attr("y", item => y(item.label))
            .attr("height", y.bandwidth())
            .attr("width", item => x(item.value))
            .attr("rx", 3)
            .attr("fill", item => item.color);

        plot.selectAll("text.value")
            .data(data)
            .join("text")
            .attr("class", "value")
            .attr("x", item => x(item.value) + 8)
            .attr("y", item => (y(item.label) || 0) + y.bandwidth() / 2)
            .attr("dy", "0.35em")
            .text(item => item.value);

        plot.append("g")
            .attr("class", "dashboard-chart-y-axis")
            .call(window.d3.axisLeft(y).tickSize(0))
            .call(axis => axis.select(".domain").remove());
        plot.append("g")
            .attr("class", "dashboard-chart-x-axis")
            .attr("transform", `translate(0,${chartHeight})`)
            .call(window.d3.axisBottom(x).tickValues(tickValues).tickFormat(window.d3.format("d")))
            .call(axis => axis.select(".domain").attr("stroke", "var(--color-border)"));
    }

    function navigateToTicketDetails(ticketId) {
        window.AdminNavigation?.navigateToTicketManagement();
        window.setTimeout(() => window.AdminTickets?.loadTicketDetails(ticketId), 200);
    }

    function navigateToInquiryDetails(inquiryId) {
        window.AdminNavigation?.navigateToInquiryManagement();
        window.setTimeout(() => window.AdminInquiries?.loadInquiryDetails(inquiryId), 200);
    }

    function initializeDashboardInteractions() {
        document.querySelectorAll("[data-page-target]").forEach(button => {
            button.addEventListener("click", () => {
                document.querySelector(`[data-page="${button.dataset.pageTarget}"]`)?.click();
            });
        });
        window.addEventListener("resize", () => {
            window.clearTimeout(resizeTimer);
            resizeTimer = window.setTimeout(() => {
                if (currentChartData && document.getElementById("dashboard-page")?.classList.contains("active")) {
                    renderCharts(currentChartData);
                }
            }, 160);
        });
    }

    initializeDashboardInteractions();

    return {
        loadEnhancedDashboardData,
        updateBasicStats,
        updatePriorityChart: summary => renderCharts(summary),
        navigateToTicketDetails,
        navigateToInquiryDetails
    };
})();
