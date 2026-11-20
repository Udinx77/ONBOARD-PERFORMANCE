Menentukan Availability setiap Perangkat Onboard

	Hitung latensi:
	- Ping ke IP yang dituju, hitung hasil ping
	- Jika latensi >=25, maka Latensi = 0
	- Jika latensi <25, maka Latensi = 1
	- Perhitungan dilakukan setiap jam
	- Presentase latensi dihitung dari frekuensi 0 dan 1

Menentukan Availability Perangkat Onboard secara keseluruhan:
	- Minimun Availability Perangkat dalam 1 siklus (1 jam): 98%
	- Persentase latensi perangkat R, D, P dihitung dari semua siklus
	 