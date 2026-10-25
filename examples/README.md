# Configuration examples

The program starts without configured addresses, network name, network password, device identity or pairing key. Use the setup dialog to choose a network name and generate a real pairing document. The dialog suggests virtual IPs 10.144.144.1 and 10.144.144.2 for a new managed network; choose another /24 if it conflicts with existing networks.

For documentation only, a pair might use `192.0.2.10` and `192.0.2.20`; a Linux board might use `198.51.100.2`. These are documentation addresses, not the virtual-address defaults. Replace them with addresses actually assigned to your own interfaces.

Do not put an exported pairing document here. The export contains a secret and belongs outside a public repository.
