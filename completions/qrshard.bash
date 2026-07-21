# Bash completion for qrshard. Source from ~/.bashrc:  source /path/to/qrshard.bash
_qrshard() {
    local cur prev commands
    COMPREPLY=()
    cur="${COMP_WORDS[COMP_CWORD]}"
    prev="${COMP_WORDS[COMP_CWORD-1]}"
    commands="encode decode send receive verify info calibrate test help"

    if [[ ${COMP_CWORD} -eq 1 ]]; then
        COMPREPLY=( $(compgen -W "${commands}" -- "${cur}") )
        return 0
    fi

    case "${COMP_WORDS[1]}" in
        encode|send)
            COMPREPLY=( $(compgen -W "-o --out -r --resolution -c --cell -b --bits -e --ecc -R --recovery -F --fountain -p --password -f --format -i --interval --camera --video --open --no-compress --interleave2" -- "${cur}") )
            ;;
        decode)
            COMPREPLY=( $(compgen -W "-o --out -p --password --session --watch --clipboard --fps" -- "${cur}") )
            ;;
        receive)
            COMPREPLY=( $(compgen -W "--device --format --screen --region --fps -o --out -p --password" -- "${cur}") )
            ;;
        verify)
            COMPREPLY=( $(compgen -W "--session --json" -- "${cur}") )
            ;;
        info)
            COMPREPLY=( $(compgen -W "--heatmap --json" -- "${cur}") )
            ;;
        calibrate)
            COMPREPLY=( $(compgen -W "-o --out -r --resolution --camera" -- "${cur}") )
            ;;
    esac

    # Fall through to filename completion for values.
    if [[ -z "${COMPREPLY}" ]]; then
        COMPREPLY=( $(compgen -f -- "${cur}") )
    fi
    return 0
}
complete -o default -F _qrshard qrshard
