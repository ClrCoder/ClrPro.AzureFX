// Copyright (c) ClrCoder community. All Rights Reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace ClrPro.AzureFX.LocalCredentialBridge;

using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;

/// <summary>
///     Owns bounded, one-time Arc authentication challenges and their file lifecycle.
/// </summary>
public sealed class ChallengeStore : BackgroundService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lifecycleGate = new();
    private readonly Dictionary<string, Challenge> _challenges = new(StringComparer.Ordinal);
    private readonly HashSet<string> _orphanFiles = new(StringComparer.Ordinal);
    private readonly LocalCredentialBridgeOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ChallengeStore> _logger;
    private readonly string _localPath;
    private Task? _stopTask;
    private bool _disposed;

    /// <summary>
    ///     Initializes a new instance of the <see cref="ChallengeStore"/> class.
    /// </summary>
    [SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1611:Element parameters should be documented", Justification = "DI dependencies.")]
    public ChallengeStore(
        IOptions<LocalCredentialBridgeOptions> options,
        TimeProvider timeProvider,
        ILogger<ChallengeStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
        _localPath = Environment.ExpandEnvironmentVariables(_options.LocalTokensPath ?? string.Empty);
    }

    /// <inheritdoc />
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.UseFileTokenAuth)
        {
            Directory.CreateDirectory(_localPath);
            foreach (var path in Directory.EnumerateFiles(_localPath, "*.key"))
            {
                if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "D", out _))
                {
                    _orphanFiles.Add(path);
                }
            }

            await CleanupAsync(cancellationToken).ConfigureAwait(false);
        }

        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Creates a challenge, or returns null when the pending-file limit is reached.
    /// </summary>
    /// <param name="cancellationToken">Request cancellation.</param>
    /// <returns>The client-visible challenge file path, or null at capacity.</returns>
    public async Task<string?> CreateAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CleanupExpired();
            if (_challenges.Count + _orphanFiles.Count >= _options.MaxPendingChallenges)
            {
                _logger.LogWarning("Pending authentication challenge limit reached.");
                return null;
            }

            var fileName = $"{Guid.NewGuid():D}.key";
            var localFile = Path.Combine(_localPath, fileName);
            var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            try
            {
                await File.WriteAllTextAsync(localFile, secret, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (!TryDelete(localFile))
                {
                    _orphanFiles.Add(localFile);
                }

                throw;
            }

            _challenges.Add(
                secret,
                new Challenge(localFile, _timeProvider.GetUtcNow().AddSeconds(_options.AuthTokenTimeoutSeconds)));
            var remoteFile = Path.Combine(_options.RemoteTokensPath!, fileName);
            return remoteFile.Contains('/', StringComparison.Ordinal) ? remoteFile.Replace('\\', '/') : remoteFile;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    ///     Atomically consumes a valid, unexpired challenge.
    /// </summary>
    /// <param name="secret">The challenge secret supplied by the client.</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    /// <returns>Whether this request consumed the challenge.</returns>
    public async Task<bool> RedeemAsync(string secret, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_challenges.TryGetValue(secret, out var challenge)
                || challenge.Consumed
                || challenge.ExpiresAt <= _timeProvider.GetUtcNow())
            {
                CleanupExpired();
                return false;
            }

            challenge.Consumed = true;
            if (TryDelete(challenge.FilePath))
            {
                _challenges.Remove(secret);
            }

            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycleGate)
        {
            return _stopTask ??= StopCoreAsync(cancellationToken);
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        base.Dispose();
        _gate.Dispose();
    }

    internal async Task CleanupAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CleanupExpired();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.UseFileTokenAuth)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await CleanupAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            foreach (var challenge in _challenges.Values)
            {
                challenge.Consumed = true;
            }

            CleanupExpired();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void CleanupExpired()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var pair in _challenges.ToArray())
        {
            if ((pair.Value.Consumed || pair.Value.ExpiresAt <= now) && TryDelete(pair.Value.FilePath))
            {
                _challenges.Remove(pair.Key);
            }
        }

        foreach (var file in _orphanFiles.ToArray())
        {
            if (TryDelete(file))
            {
                _orphanFiles.Remove(file);
            }
        }
    }

    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "Only generated GUID filenames and startup-discovered GUID .key files in the configured directory are deleted; headers only select dictionary entries.")]
    private bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Could not delete a challenge file; cleanup will retry.");
            return false;
        }
    }

    private sealed class Challenge(string filePath, DateTimeOffset expiresAt)
    {
        public string FilePath { get; } = filePath;

        public DateTimeOffset ExpiresAt { get; } = expiresAt;

        public bool Consumed { get; set; }
    }
}
