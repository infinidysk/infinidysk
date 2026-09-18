import { describe, expect, it } from "vitest";
import {
  isMediaServersSettingsUpdated,
  isMediaServersSettingsValid,
  parseMediaServerConfig,
} from "./media-servers";

const MASK =
  "__NZBDAV_SECRET_MASK_V1__:AAAAAAAAAAAAAAAAAAAAAA.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

function config(value: unknown): Record<string, string> {
  return { "media-servers.instances": JSON.stringify(value) };
}

describe("media server settings", () => {
  it("parses the backend multi-instance shape", () => {
    const parsed = parseMediaServerConfig(
      config({
        Instances: [
          {
            Id: "11111111-1111-1111-1111-111111111111",
            Type: "Plex",
            Name: "Home Plex",
            BaseUrl: "http://plex:32400",
            Token: MASK,
            Enabled: true,
            PathMappings: [],
          },
        ],
      })["media-servers.instances"],
    );

    expect(parsed.Instances).toHaveLength(1);
    expect(parsed.Instances[0]?.Type).toBe("Plex");
  });

  it("accepts a masked saved token without exposing plaintext", () => {
    expect(
      isMediaServersSettingsValid(
        config({
          Instances: [
            {
              Id: "11111111-1111-1111-1111-111111111111",
              Type: "Jellyfin",
              Name: "Home Jellyfin",
              BaseUrl: "https://jellyfin.example.test",
              Token: MASK,
              Enabled: true,
              PathMappings: [
                { MediaServerPrefix: "/media", InfiniDyskPrefix: "/mnt/media" },
              ],
            },
          ],
        }),
      ),
    ).toBe(true);
  });

  it("rejects unsafe URLs and incomplete exact path mappings", () => {
    expect(
      isMediaServersSettingsValid(
        config({
          Instances: [
            {
              Id: "11111111-1111-1111-1111-111111111111",
              Type: "Plex",
              Name: "Plex",
              BaseUrl: "https://user:pass@plex.example.test?token=secret",
              Token: "token",
              Enabled: true,
              PathMappings: [],
            },
          ],
        }),
      ),
    ).toBe(false);

    expect(
      isMediaServersSettingsValid(
        config({
          Instances: [
            {
              Id: "11111111-1111-1111-1111-111111111111",
              Type: "Emby",
              Name: "Emby",
              BaseUrl: "http://emby:8096",
              Token: "token",
              Enabled: true,
              PathMappings: [{ MediaServerPrefix: "/media", InfiniDyskPrefix: "relative" }],
            },
          ],
        }),
      ),
    ).toBe(false);
  });

  it("allows a disabled instance without a token", () => {
    expect(
      isMediaServersSettingsValid(
        config({
          Instances: [
            {
              Id: "11111111-1111-1111-1111-111111111111",
              Type: "Plex",
              Name: "Disabled Plex",
              BaseUrl: "http://plex:32400",
              Token: "",
              Enabled: false,
              PathMappings: [],
            },
          ],
        }),
      ),
    ).toBe(true);
  });

  it("tracks the structured config as one settings value", () => {
    const original = config({ Instances: [] });
    const changed = config({
      Instances: [
        {
          Id: "11111111-1111-1111-1111-111111111111",
          Type: "Plex",
          Name: "Plex",
          BaseUrl: "http://plex:32400",
          Token: "token",
          Enabled: true,
          PathMappings: [],
        },
      ],
    });

    expect(isMediaServersSettingsUpdated(original, original)).toBe(false);
    expect(isMediaServersSettingsUpdated(original, changed)).toBe(true);
  });
});
