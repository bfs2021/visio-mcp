// One bundle, two pages: the editor the person works in, and the agent-only view the host opens on first use (?view=agent).
if (new URLSearchParams(location.search).get('view') === 'agent') void import('./agentMain');
else void import('./main');
