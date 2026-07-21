# PowerShell completion for qrshard. Dot-source from your $PROFILE:  . /path/to/qrshard.ps1
Register-ArgumentCompleter -Native -CommandName qrshard -ScriptBlock {
    param($wordToComplete, $commandAst, $cursorPosition)

    $commands = 'encode', 'decode', 'send', 'receive', 'verify', 'info', 'calibrate', 'test', 'help'
    $options = @{
        encode    = '-o', '--out', '-r', '--resolution', '-c', '--cell', '-b', '--bits', '-e', '--ecc',
                    '-R', '--recovery', '-F', '--fountain', '-p', '--password', '-f', '--format',
                    '-i', '--interval', '--camera', '--video', '--open', '--no-compress', '--interleave2'
        send      = $null # same as encode
        decode    = '-o', '--out', '-p', '--password', '--session', '--watch', '--clipboard', '--fps'
        receive   = '--device', '--format', '--screen', '--region', '--fps', '-o', '--out', '-p', '--password'
        verify    = '--session', '--json'
        info      = '--heatmap', '--json'
        calibrate = '-o', '--out', '-r', '--resolution', '--camera'
    }
    $options['send'] = $options['encode']

    $tokens = $commandAst.CommandElements | ForEach-Object { $_.ToString() }
    if ($tokens.Count -le 1 -or ($tokens.Count -eq 2 -and $wordToComplete)) {
        $commands | Where-Object { $_ -like "$wordToComplete*" } |
            ForEach-Object { [System.Management.Automation.CompletionResult]::new($_) }
        return
    }
    $sub = $tokens[1]
    if ($options.ContainsKey($sub)) {
        $options[$sub] | Where-Object { $_ -like "$wordToComplete*" } |
            ForEach-Object { [System.Management.Automation.CompletionResult]::new($_) }
    }
}
