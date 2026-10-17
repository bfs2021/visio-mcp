import type { AppError, ErrorCode } from '../contracts/generated';

export type Result<T> = { ok: true; value: T } | { ok: false; error: AppError };

export const ok = <T>(value: T): Result<T> => ({ ok: true, value });

export function fail<T = never>(
  code: ErrorCode,
  message: string,
  extra: { retryable?: boolean; details?: Record<string, unknown>; outcome?: 'unknown' | 'none' } = {},
): Result<T> {
  const error: AppError = { code, message, retryable: extra.retryable ?? false };
  if (extra.details) error.details = extra.details;
  if (extra.outcome) error.outcome = extra.outcome;
  return { ok: false, error };
}
