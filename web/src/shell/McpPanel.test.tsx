import { describe, expect, it } from 'vitest';
import type { McpStatus } from '../contracts.gen';
import { claudeCommand, jsonConfig, mcpLabel } from './McpPanel';

const status: McpStatus = {
  available: true,
  enabled: true,
  running: true,
  port: 47600,
  url: 'http://127.0.0.1:47600/mcp',
  token: 'tok_123',
  allowDacDownload: false,
  error: null,
};

describe('MCP client configuration', () => {
  it('gives Claude Code the URL and the token as a bearer header', () => {
    expect(claudeCommand(status)).toBe(
      'claude mcp add --transport http dump-debugger http://127.0.0.1:47600/mcp --header "Authorization: Bearer tok_123"',
    );
  });

  it('gives other clients an mcpServers entry', () => {
    expect(JSON.parse(jsonConfig(status))).toEqual({
      mcpServers: {
        'dump-debugger': {
          type: 'http',
          url: 'http://127.0.0.1:47600/mcp',
          headers: { Authorization: 'Bearer tok_123' },
        },
      },
    });
  });
});

describe('MCP status bar label', () => {
  it('reads off before the status is known and when disabled', () => {
    expect(mcpLabel(null)).toBe('MCP: off');
    expect(mcpLabel({ ...status, enabled: false, running: false })).toBe('MCP: off');
  });

  it('reads on only while the server is actually listening', () => {
    expect(mcpLabel(status)).toBe('MCP: on');
    expect(mcpLabel({ ...status, running: false, error: 'Port 47600 is already in use.' })).toBe('MCP: not running');
  });
});
