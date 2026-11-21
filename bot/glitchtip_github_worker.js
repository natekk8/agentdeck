/**
 * Cloudflare Worker webhook bridge: GlitchTip Alert Webhook -> GitHub Issues via agentdeckbot.
 *
 * Deploy free on Cloudflare Workers (or any serverless runtime):
 * 1. Set secrets:
 *    - GITHUB_TOKEN : Fine-grained PAT of @agentdeckbot with `Issues: Read & Write` on `natekk8/agentdeck`
 *    - WEBHOOK_SECRET (optional) : shared query secret `?token=...` configured in GlitchTip Alert Webhook URL
 * 2. In GlitchTip -> Project Settings -> Alerts -> Create Alert -> Add Webhook:
 *    https://<your-worker>.workers.dev/?token=<WEBHOOK_SECRET>
 */

const REPO = "natekk8/agentdeck";

export default {
  async fetch(request, env) {
    if (request.method !== "POST") {
      return new Response("Method Not Allowed", { status: 405 });
    }

    const url = new URL(request.url);
    if (env.WEBHOOK_SECRET && url.searchParams.get("token") !== env.WEBHOOK_SECRET) {
      return new Response("Unauthorized", { status: 401 });
    }

    const payload = await request.json().catch(() => ({}));
    const event = payload.event || payload.data?.event || {};
    const tags = Object.fromEntries((event.tags || []).map((t) => [t.key, t.value]));
    const extra = event.extra || event.contexts || {};
    const attachment = (payload.attachments && payload.attachments[0]) || {};

    const kind = (tags.report_kind || extra.kind || "bug").toLowerCase();
    const rawTitle = extra.title || event.title || attachment.title || payload.alias || "AgentDeck Report";
    const description = extra.description || attachment.text || event.message || "Submitted via AgentDeck Reporter.";
    const version = tags.app_version || event.release || "unknown";
    const os = tags.os || "Windows";
    const lang = tags.lang || "en";
    const contact = extra.contact ? `\n\n**Contact:** ${extra.contact}` : "";
    const diag = extra.diagnostics
      ? `\n\n<details>\n<summary>Sanitized Diagnostics</summary>\n\n\`\`\`\n${String(extra.diagnostics).slice(0, 3500)}\n\`\`\`\n</details>`
      : "";

    const label = kind === "feature" ? "enhancement" : kind === "bug" ? "bug" : "feedback";
    const prefix = kind === "feature" ? "[Feature]" : kind === "bug" ? "[Bug]" : "[Feedback]";
    const issueTitle = `${prefix} ${String(rawTitle).slice(0, 120)}`;
    const issueBody =
      `${description}${contact}\n\n---\n` +
      `**Version:** \`${version}\` | **OS:** \`${os}\` | **Lang:** \`${lang}\` | **GlitchTip Event:** \`${event.event_id || "n/a"}\`` +
      diag;

    const ghRes = await fetch(`https://api.github.com/repos/${REPO}/issues`, {
      method: "POST",
      headers: {
        Authorization: `Bearer ${env.GITHUB_TOKEN}`,
        Accept: "application/vnd.github+json",
        "User-Agent": "agentdeckbot-glitchtip-bridge",
        "Content-Type": "application/json",
      },
      body: JSON.stringify({
        title: issueTitle,
        body: issueBody,
        labels: [label],
      }),
    });

    if (!ghRes.ok) {
      const errText = await ghRes.text();
      return new Response(`GitHub API error: ${errText}`, { status: 502 });
    }

    const created = await ghRes.json();
    return Response.json({ ok: true, issue_number: created.number, html_url: created.html_url });
  },
};
