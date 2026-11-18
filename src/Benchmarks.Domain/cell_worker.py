#!/usr/bin/env python3
"""One A21 cell in an externally supervised process; A21 remains unchanged."""
import argparse
import asyncio
from pathlib import Path
import traceback

from common import load, load_a21, save


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--plan', required=True, type=Path)
    parser.add_argument('--route', required=True)
    parser.add_argument('--repetition', required=True, type=int)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--a21', required=True, type=Path)
    args = parser.parse_args()
    a21 = load_a21(args.a21)
    plan = load(args.plan)
    route = next(value for value in plan['routes'] if value['id'] == args.route)
    try:
        asyncio.run(a21.run_cell(plan, route, args.repetition, args.plan.parent, args.output))
        return 0
    except BaseException as error:
        save(args.output.with_suffix('.worker-failure.json'), {'type': type(error).__name__,
            'message': str(error)[:1024], 'traceback': traceback.format_exc()[:16384],
            'claim': 'Failed/incomplete cell; never a benchmark success'})
        raise


if __name__ == '__main__':
    raise SystemExit(main())
