# Dot-sourced by every test file that starts processes. On Windows, Start-Process -WindowStyle Hidden gives the
# child a console of its own. Off Windows there is no such parameter, and a child inherits this process's stdio:
# under run-all's Start-Job that is the job's channel to its parent, so a stub's first line kills the job. The
# wrapper drops -WindowStyle and gives the child /dev/null for input and temp files for any output not redirected
# already. On Windows nothing is defined.
if (-not $IsWindows) {
    function global:Start-Process {
        param(
            [Parameter(Position = 0)] [string] $FilePath,
            [Parameter(Position = 1)] [string[]] $ArgumentList,
            [string] $WindowStyle,
            [string] $WorkingDirectory,
            [string] $RedirectStandardOutput,
            [string] $RedirectStandardError,
            [string] $RedirectStandardInput,
            [switch] $PassThru,
            [switch] $NoNewWindow,
            [switch] $Wait
        )
        $p = @{} + $PSBoundParameters
        $p.Remove('WindowStyle')
        if (-not $p['RedirectStandardInput']) { $p.RedirectStandardInput = '/dev/null' }
        if (-not $p['RedirectStandardOutput']) { $p.RedirectStandardOutput = [IO.Path]::GetTempFileName() }
        if (-not $p['RedirectStandardError']) { $p.RedirectStandardError = [IO.Path]::GetTempFileName() }
        Microsoft.PowerShell.Management\Start-Process @p
    }
}
