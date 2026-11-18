"use strict";
document.querySelectorAll('.toggle-password').forEach(button => button.addEventListener('click', () => {
    const input = button.parentElement.querySelector('input');
    const show = input.type === 'password';
    input.type = show ? 'text' : 'password';
    button.setAttribute('aria-label', show ? '隐藏密码或 Secret' : '显示密码或 Secret');
    button.setAttribute('aria-pressed', String(show));
}));
const menu = document.querySelector('.mobile-menu');
menu?.addEventListener('click', () => {
    const open = document.querySelector('#sidebar').classList.toggle('open');
    menu.setAttribute('aria-expanded', String(open));
});
document.addEventListener('keydown', event => {
    if (event.key === 'Escape') { document.querySelector('#sidebar')?.classList.remove('open'); menu?.setAttribute('aria-expanded', 'false'); }
});
document.addEventListener('click', event => {
    if (menu && !menu.contains(event.target) && !document.querySelector('#sidebar')?.contains(event.target)) {
        document.querySelector('#sidebar')?.classList.remove('open'); menu.setAttribute('aria-expanded', 'false');
    }
});
const dialog = document.querySelector('#confirm-dialog');
const messageDialog = document.querySelector('#message-detail-dialog');
const messageDialogBody = document.querySelector('#message-detail-body');
document.addEventListener('click', event => {
    const trigger = event.target.closest?.('[data-message-template]');
    if (!trigger || !messageDialog || messageDialog.open) return;
    const template = document.getElementById(trigger.dataset.messageTemplate);
    if (!(template instanceof HTMLTemplateElement)) return;
    messageDialogBody.replaceChildren(template.content.cloneNode(true));
    messageDialog.showModal();
    document.body.classList.add('message-modal-open');
    messageDialogBody.scrollTop = 0;
});
document.querySelectorAll('[data-close-message-detail]').forEach(button => button.addEventListener('click', () => messageDialog.close()));
messageDialog?.addEventListener('click', event => {
    if (event.target !== messageDialog) return;
    const bounds = messageDialog.getBoundingClientRect();
    if (event.clientX < bounds.left || event.clientX > bounds.right || event.clientY < bounds.top || event.clientY > bounds.bottom) messageDialog.close();
});
messageDialog?.addEventListener('close', () => {
    document.body.classList.remove('message-modal-open');
    messageDialogBody.replaceChildren();
});
let deleteForm;
document.querySelectorAll('form[data-delete-name]').forEach(form => form.addEventListener('submit', event => {
    event.preventDefault(); deleteForm = form;
    document.querySelector('#confirm-title').textContent = `删除这个${form.dataset.deleteKind || '机器人'}？`;
    document.querySelector('#confirm-note').textContent = form.dataset.deleteNote || '此操作会移除配置，历史消息记录仍会保留。';
    document.querySelector('#confirm-description').textContent = `即将删除「${form.dataset.deleteName}」。`;
    dialog.showModal();
}));
document.querySelector('#confirm-cancel')?.addEventListener('click', () => { dialog.close(); deleteForm = null; });
document.querySelector('#confirm-accept')?.addEventListener('click', event => { event.target.disabled = true; deleteForm?.submit(); });
const content = document.querySelector('#message-content');
const sendButton = document.querySelector('#send-button');
const botSelect = document.querySelector('#Input_RobotId');
const targetType = document.querySelector('#Input_TargetType');
if (content) {
    const update = () => {
        const bytes = new TextEncoder().encode(content.value).length;
        const counter = document.querySelector('#byte-count');
        counter.textContent = `${bytes} / 4096 字节`;
        counter.classList.toggle('over-limit', bytes > 4096);
        content.setCustomValidity(bytes > 4096 ? '消息内容不能超过 4096 个 UTF-8 字节。' : '');
        document.querySelector('#message-preview').textContent = content.value;
        document.querySelector('#message-preview').hidden = !content.value;
        document.querySelector('#preview-placeholder').hidden = !!content.value;
        sendButton.disabled = bytes > 4096 || botSelect.options.length <= 1;
    };
    content.addEventListener('input', update); update();
    const updateTarget = () => {
        const group = targetType?.value === 'group';
        document.querySelector('#user-target-field').hidden = group;
        document.querySelector('#group-target-field').hidden = !group;
        const userInput = document.querySelector('#Input_UserId');
        const chatInput = document.querySelector('#Input_ChatId');
        userInput.disabled = group; userInput.required = !group;
        chatInput.disabled = !group; chatInput.required = group;
        document.querySelector('#target-mode-badge').textContent = group ? '群聊发送' : '单聊发送';
        document.querySelector('#target-help').textContent = group
            ? '请确认机器人已加入目标群，使用群聊消息回调中的实际 chatid。每次仅支持一个群聊。'
            : '接收人应在机器人可见范围内，并已打开对应机器人会话。User ID 不是姓名、昵称或微信号。';
        const name = botSelect.selectedOptions[0]?.textContent || '企微机器人';
        const heading = document.querySelector('#preview-bot');
        heading.replaceChildren(document.createTextNode(botSelect.value ? name : '企微机器人'));
        const small = document.createElement('small'); small.textContent = group ? '群聊机器人消息' : '单聊机器人消息'; heading.append(small);
    };
    botSelect?.addEventListener('change', updateTarget);
    targetType?.addEventListener('change', updateTarget);
    updateTarget();
}
document.querySelectorAll('form[data-submit-lock]').forEach(form => form.addEventListener('submit', event => {
    if (form.dataset.submitted) { event.preventDefault(); return; }
    if (!form.checkValidity()) return;
    form.dataset.submitted = 'true';
    const button = form.querySelector('button[type="submit"]');
    if (button) { button.disabled = true; button.textContent = form.id === 'send-form' ? '正在等待企微回执…' : '正在提交…'; }
}));
window.addEventListener('pageshow', event => {
    if (event.persisted) window.location.reload();
});
