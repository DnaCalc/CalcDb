#!/usr/bin/env bash
# First reading run for CalcDb planning (steps 1-3 of the budget plan, ~$23).
# Start detached:  tmux new -d -s r1 ~/cleanroom/reader/run-r1.sh
# Watch:           tmux attach -t r1   (Ctrl+B then D to detach)   or  tail -f ~/cleanroom/out/r1.log
set -euo pipefail
cd "$(dirname "$0")"
LOG=~/cleanroom/out/r1.log
mkdir -p ~/cleanroom/out
{
  echo "=== r1 start $(date -Is)"
  python3 reader.py map    --run r1 --profile survey --model deepseek/deepseek-v4-pro   --budget 16
  python3 reader.py map    --run r1 --profile rest   --model deepseek/deepseek-v4-flash --budget 7
  python3 reader.py reduce --run r1 --reduce-model deepseek/deepseek-v4-pro --budget 6
  python3 reader.py synth  --run r1 --reduce-model deepseek/deepseek-v4-pro --budget 2
  python3 reader.py leakcheck --run r1
  python3 reader.py spend
  echo "=== r1 exit $(date -Is)"
} 2>&1 | tee -a "$LOG"
