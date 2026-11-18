"use strict";
const replyEnabled = document.querySelector('#AutoReplyEnabled');
const replyMode = document.querySelector('#ReplyMode');
function updateReplySettings() {
    if (!replyEnabled) return;
    document.querySelector('#auto-reply-options').hidden = !replyEnabled.checked;
    document.querySelector('#knowledge-reply-help').hidden = replyMode.value !== 'knowledge';
    document.querySelector('#ai-reply-options').hidden = replyMode.value !== 'ai';
}
replyEnabled?.addEventListener('change', updateReplySettings); replyMode?.addEventListener('change', updateReplySettings); updateReplySettings();
const priorities = document.querySelector('#model-priority-list');
function updatePriorities() {
    if (!priorities) return;
    const rows = [...priorities.children];
    rows.forEach((row,index) => {
        row.querySelector('.priority-number').textContent = String(index + 1);
        row.querySelector('select').setAttribute('aria-label',`第 ${index + 1} 优先模型`);
        row.querySelector('[data-priority-up]').disabled = index === 0;
        row.querySelector('[data-priority-down]').disabled = index === rows.length - 1;
        row.querySelector('[data-priority-remove]').disabled = rows.length === 1;
    });
    document.querySelector('#add-priority-model').disabled = rows.length >= 8;
}
priorities?.addEventListener('click',event => {
    const button = event.target.closest('button'); if (!button) return;
    const row = button.closest('.model-priority-row'); if (!row) return;
    if (button.hasAttribute('data-priority-up') && row.previousElementSibling) priorities.insertBefore(row,row.previousElementSibling);
    if (button.hasAttribute('data-priority-down') && row.nextElementSibling) priorities.insertBefore(row.nextElementSibling,row);
    if (button.hasAttribute('data-priority-remove') && priorities.children.length > 1) row.remove();
    updatePriorities();
});
document.querySelector('#add-priority-model')?.addEventListener('click', () => {
    if (priorities.children.length >= 8) return;
    priorities.append(document.querySelector('#model-priority-template').content.cloneNode(true)); updatePriorities();
});
updatePriorities();
const presets = {
    openai:{provider:'OpenAI',endpoint:'https://api.openai.com/v1/responses',style:'responses'},
    deepseek:{provider:'DeepSeek',endpoint:'https://api.deepseek.com/chat/completions',style:'chat'},
    zhipu:{provider:'智谱',endpoint:'https://open.bigmodel.cn/api/paas/v4/chat/completions',style:'chat'}
};
document.querySelectorAll('[data-provider-preset]').forEach(button => button.addEventListener('click', () => {
    const preset=presets[button.dataset.providerPreset]; if (!preset) return;
    document.querySelector('#Provider').value=preset.provider; document.querySelector('#Endpoint').value=preset.endpoint; document.querySelector('#ApiStyle').value=preset.style;
}));
