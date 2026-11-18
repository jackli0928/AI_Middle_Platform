"use strict";
const live = document.querySelector('#inbox-live');
const inboxStatus = document.querySelector('#inbox-refresh');
const connectionNames = { online:'已连接',connecting:'连接中',authenticating:'认证中',reconnecting:'重连中',disabled:'已停用',standby:'其他实例连接中',conflict:'连接被接管',error:'配置异常' };
let refreshing = false;
async function refreshInbox() {
    if (!live?.checked || document.hidden || refreshing || document.activeElement?.closest('.inbox-filters, #inbox-results')) return;
    refreshing = true;
    try {
        const [rows, connections] = await Promise.all([fetch('/Inbox/Rows' + location.search), fetch('/Inbox/Connections')]);
        if (rows.redirected || connections.redirected) { live.checked = false; inboxStatus.textContent = '登录可能已过期，请刷新页面后重新登录。'; return; }
        if (!rows.ok || !connections.ok || rows.headers.get('X-Inbox-Partial') !== '1') throw new Error('refresh');
        const results = document.querySelector('#inbox-results');
        const template = document.createElement('template');
        template.innerHTML = await rows.text();
        if (results.innerHTML !== template.innerHTML) results.replaceChildren(template.content);
        const states = await connections.json();
        document.querySelector('#online-count').textContent = `${states.filter(x => x.state === 'online').length} / ${states.length} 已连接`;
        for (const state of states) {
            const row = document.querySelector(`[data-robot-id="${state.robotId}"]`);
            if (!row) continue;
            const badge = row.querySelector('[data-connection-state]');
            badge.textContent = connectionNames[state.state] || '配置异常';
            badge.className = 'badge ' + (state.state === 'online' ? 'success' : ['error','conflict'].includes(state.state) ? 'danger' : state.state === 'disabled' ? 'neutral' : 'warning');
            row.querySelector('[data-connection-description]').textContent = state.description;
            row.querySelector('button').disabled = state.state === 'disabled';
            row.querySelector('[data-connection-heartbeat]').textContent = '最近心跳：' + (state.lastHeartbeatAt ? new Date(state.lastHeartbeatAt).toLocaleTimeString('zh-CN',{timeZone:'Asia/Shanghai',hour12:false}) : '等待心跳');
        }
        inboxStatus.textContent = '已刷新 · ' + new Date().toLocaleTimeString('zh-CN', {timeZone:'Asia/Shanghai',hour12:false}) + '（北京时间）';
    } catch { inboxStatus.textContent = '暂时无法刷新，将在下次自动重试。'; }
    finally { refreshing = false; }
}
if (live) { setInterval(refreshInbox,15000); live.addEventListener('change', () => { if (live.checked) refreshInbox(); }); }
