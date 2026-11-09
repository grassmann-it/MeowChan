# Synthetic disclaimer boundary

If a response contains only a disclaimer/interstitial and no supported post
container, return `user_action_required`. Do not append `disclaimer=seen`, send
cookies, or assume that a user accepted a warning.
