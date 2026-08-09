import os
import sys

# Make the worker modules importable without installing them as a package.
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
