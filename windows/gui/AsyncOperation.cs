// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

namespace TruckersMP.Proxy;

internal sealed class AsyncOperation
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task RunAsync(Func<Task> operation)
    {
        await _gate.WaitAsync();
        try { await operation(); }
        finally { _gate.Release(); }
    }
}
