import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

/**
 * The ticket endpoint's one job beyond minting: telling the client whether a
 * ticket-less answer is worth retrying. The client treats a denial as terminal
 * and deliberately silent, so a fault misfiled as a denial takes realtime down
 * with no error shown and no recovery short of a page reload.
 */

const probe = vi.fn();

vi.mock("@nocturne/bridge/ticket", () => ({
  signHandshakeTicket: (_secret: string, host: string) => `ticket-for-${host}`,
}));
vi.mock("$lib/server/api-client-factory", () => ({
  getApiBaseUrl: () => apiBaseUrl,
  createServerHttpClient: () => ({ fetch: probe }),
}));
vi.mock("$lib/server/instance-key", () => ({
  getHashedInstanceKey: () => "hashed-key",
}));
vi.mock("$lib/server/request-host", () => ({
  getEffectiveHost: () => effectiveHost,
  getOriginalProto: () => "https",
}));
vi.mock("$lib/config/auth-cookies", () => ({
  AUTH_COOKIE_NAMES: {
    accessToken: "access",
    refreshToken: "refresh",
    guestSession: "guest",
    platformAccess: "platform",
  },
}));

let apiBaseUrl: string | undefined = "http://api.internal";
let effectiveHost: string | undefined = "sleepy.nocturne.run";

const { GET } = await import("./+server");

/** The endpoint's answer, which is always a 200 whatever it decided. */
async function mint(): Promise<{ token: string | null; retry?: boolean }> {
  const event = {
    request: new Request("https://sleepy.nocturne.run/realtime/ticket"),
    cookies: { get: () => undefined },
    fetch: vi.fn(),
    locals: { rawSetCookies: [] },
  };
  // The handler only uses the slice of RequestEvent built above.
  const res = await GET(event as unknown as Parameters<typeof GET>[0]);
  expect(res.status).toBe(200);
  return res.json();
}

beforeEach(() => {
  process.env.INSTANCE_KEY = "an-instance-signing-secret";
  apiBaseUrl = "http://api.internal";
  effectiveHost = "sleepy.nocturne.run";
  probe.mockReset();
});

afterEach(() => {
  delete process.env.INSTANCE_KEY;
});

describe("realtime ticket endpoint", () => {
  it("mints a ticket when the read policy admits the caller", async () => {
    probe.mockResolvedValue({ ok: true, status: 200 });

    expect(await mint()).toEqual({ token: "ticket-for-sleepy.nocturne.run" });
  });

  it("reports a refused read as a denial the client should not retry", async () => {
    probe.mockResolvedValue({ ok: false, status: 401 });

    expect(await mint()).toEqual({ token: null, retry: false });
  });

  it("reports an API fault as retryable", async () => {
    probe.mockResolvedValue({ ok: false, status: 503 });

    expect(await mint()).toEqual({ token: null, retry: true });
  });

  it("reports an unreachable API as retryable", async () => {
    probe.mockRejectedValue(new Error("network"));

    expect(await mint()).toEqual({ token: null, retry: true });
  });

  it.each([
    ["no signing secret", () => (process.env.INSTANCE_KEY = "")],
    ["no API url", () => (apiBaseUrl = undefined)],
    ["no resolvable host", () => (effectiveHost = undefined)],
  ])(
    "reports an instance with %s as retryable, not as a denial",
    async (_case, misconfigure) => {
      misconfigure();

      // A deployment fault, not a per-user policy decision: filing it as a
      // denial would latch every viewer into a terminal, silent "realtime not
      // permitted" that survives the operator fixing the configuration.
      expect(await mint()).toEqual({ token: null, retry: true });
      expect(probe).not.toHaveBeenCalled();
    }
  );
});
