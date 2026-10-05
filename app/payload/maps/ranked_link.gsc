// ranked_link.gsc
// Lien entre les deux joueurs du mod ranked (BO1 Zombies / Plutonium).
//
// Necessite le plugin t5-gsc-utils.dll dans Plutonium\plugins\ (lecture/ecriture de fichiers).
// Les fichiers sont dans Plutonium\storage\t5\ranked\ :
//
//   state.txt     ecrit par le mod, lu par l'app compagnon. Une ligne :
//                 round;zone;a_terre;temps_ms;termine;temps_final_ms;seed;objectif;pret
//   go.txt        ecrit par l'app compagnon quand les deux joueurs sont prets. Contient la seed du match.
//   opponent.txt  ecrit par l'app compagnon, lu par le mod. Une ligne :
//                 round;zone;a_terre;termine
//                 (zone = "none" si inconnue)
//
// Les memes infos existent aussi en dvars, pour tester a la main dans la console.
// Les dvars ranked_opp_* ne sont lues que si opponent.txt n'existe pas.
//
// SORTIE (ecrit par le mod, lu par l'app compagnon) :
//   ranked_my_round        round actuel
//   ranked_my_zone         zone actuelle (ex : foyer_zone)
//   ranked_my_down         1 si a terre
//   ranked_my_time         temps de jeu en secondes
//   ranked_my_finished     1 quand le round objectif est atteint
//   ranked_my_finish_time  temps final en millisecondes
//
// ENTREE (ecrit par l'app compagnon ou a la main dans la console, lu par le mod) :
//   ranked_opp_round       round de l'adversaire (0 = pas d'adversaire, HUD cache)
//   ranked_opp_zone        zone de l'adversaire
//   ranked_opp_down        1 si l'adversaire est a terre
//   ranked_opp_finished    1 quand l'adversaire a atteint le round objectif
//
// REGLAGE :
//   ranked_goal            round a atteindre (30 par defaut)

#include maps\_utility;
#include common_scripts\utility;

rr_link_init()
{
	if ( isDefined( level.rr_link_started ) )
	{
		return;
	}
	level.rr_link_started = true;

	level thread rr_link_main();
}

// Anti-record : force l'affichage des checksums Plutonium tant que le mod est installe.
// Les scripts modifies changent ces checksums, donc une partie jouee avec le mod est
// reconnaissable sur une video et ne peut pas passer pour une partie normale.
rr_force_checksums()
{
	setDvar( "cg_drawChecksums", 1 );
	setDvar( "cg_flashScriptHashes", 1 );
}

// Reapplique en continu : le joueur ne peut pas les couper depuis la console.
rr_checksum_guard()
{
	while ( 1 )
	{
		rr_force_checksums();
		wait 0.5;
	}
}

rr_link_main()
{
	rr_force_checksums();
	level thread rr_checksum_guard();

	// Remise a zero : les dvars gardent leur valeur d'une partie a l'autre.
	setDvar( "ranked_opp_round", 0 );
	setDvar( "ranked_opp_zone", "" );
	setDvar( "ranked_opp_down", 0 );
	setDvar( "ranked_opp_finished", 0 );
	setDvar( "ranked_my_round", 0 );
	setDvar( "ranked_my_zone", "" );
	setDvar( "ranked_my_down", 0 );
	setDvar( "ranked_my_time", 0 );
	setDvar( "ranked_my_finished", 0 );
	setDvar( "ranked_my_finish_time", 0 );

	// Objectif : celui du match en cours (ranked/match.txt = "seed;objectif"), sinon la dvar, sinon 30.
	goal = 0;
	in_match = false;
	if ( fileExists( "ranked/match.txt" ) )
	{
		tokens = strTok( readFile( "ranked/match.txt" ), ";" );
		if ( tokens.size >= 2 )
		{
			goal = int( tokens[1] );
			in_match = true;
		}
	}

	// Le dossier ranked n'existe que pendant un match : l'app compagnon le cree quand un match est
	// trouve et le supprime a la fin. Hors match, le mod ne lit et n'ecrit aucun fichier.
	files_write = in_match;		// ecriture de state.txt
	files_reads = -1;			// lectures d'opponent.txt restantes (-1 = sans limite)
	if ( in_match )
	{
		if ( fileExists( "ranked/opponent.txt" ) )
		{
			// Evite d'afficher l'adversaire de la partie precedente.
			removeFile( "ranked/opponent.txt" );
		}
		if ( fileExists( "ranked/go.txt" ) )
		{
			removeFile( "ranked/go.txt" );
		}
		writeFile( "ranked/state.txt", "0;none;0;0;0;0;0;0;0" );
	}
	if ( goal <= 0 )
	{
		goal = getDvarInt( "ranked_goal" );
	}
	if ( goal <= 0 )
	{
		goal = 30;
	}

	while ( get_players().size == 0 )
	{
		wait 0.5;
	}
	player = get_players()[0];

	// En match : aucun zombie n'apparait tant que les deux joueurs ne sont pas prets.
	// Le drapeau est coupe des qu'il existe, avant le debut du round 1.
	if ( in_match )
	{
		while ( !isDefined( level.flag ) || !isDefined( level.flag["spawn_zombies"] ) )
		{
			wait 0.05;
		}
		flag_clear( "spawn_zombies" );
	}

	while ( !rr_flag_is_set( "begin_spawning" ) )
	{
		if ( in_match )
		{
			player freezeControls( true );
		}
		wait 0.1;
	}

	if ( in_match )
	{
		rr_wait_for_go( player, goal );
	}

	start_time = getTime();

	hud_round = rr_link_hud( player, 70, 1.4 );
	hud_zone = rr_link_hud( player, 86, 1.2 );
	hud_state = rr_link_hud( player, 100, 1.2 );
	hud_result = rr_link_hud( player, 120, 1.8 );

	last_round = -1;
	last_zone = "?";
	last_state = -1;
	finished = false;
	lost = false;
	finish_time = 0;
	gave_up = false;		// mort ou abandon : defaite immediate
	opp_out = false;		// l'adversaire est mort, a abandonne ou a quitte : victoire

	level.rr_surrender = false;
	level.rr_dead = false;
	if ( in_match )
	{
		// Commande console "surrender" (fournie par le plugin t5-gsc-utils).
		addCommand( "surrender", ::rr_cmd_surrender );
		level thread rr_watch_game_over();
	}

	opp_round = 0;
	opp_zone = "";
	opp_down = 0;
	opp_finished = 0;

	while ( 1 )
	{
		// ---------------- mon etat -> dvars ----------------
		my_round = 0;
		if ( isDefined( level.round_number ) )
		{
			my_round = level.round_number;
		}

		my_zone = player maps\_zombiemode_utility::get_current_zone();
		if ( !isDefined( my_zone ) )
		{
			my_zone = "";
		}

		my_down = 0;
		if ( player maps\_laststand::player_is_in_laststand() )
		{
			my_down = 1;
		}

		elapsed = getTime() - start_time;

		setDvar( "ranked_my_round", my_round );
		setDvar( "ranked_my_zone", my_zone );
		setDvar( "ranked_my_down", my_down );

		if ( !finished )
		{
			setDvar( "ranked_my_time", int( elapsed / 1000 ) );

			if ( my_round >= goal && !gave_up )
			{
				finished = true;
				setDvar( "ranked_my_finished", 1 );
				setDvar( "ranked_my_finish_time", elapsed );
				finish_time = elapsed;

				if ( !lost )
				{
					hud_result.color = ( 0.3, 1, 0.3 );
					hud_result setText( "VICTORY - round " + goal + " in " + rr_format_time( elapsed ) );
				}
				else
				{
					hud_result setText( "Round " + goal + " reached in " + rr_format_time( elapsed ) );
				}
			}
		}

		// ---------------- mort ou abandon ----------------
		if ( in_match && !finished && !lost && !opp_out && !gave_up && ( level.rr_surrender || level.rr_dead ) )
		{
			gave_up = true;
			hud_result.color = ( 1, 0.3, 0.3 );
			if ( level.rr_surrender )
			{
				hud_result setText( "DEFEAT - you surrendered" );
			}
			else
			{
				hud_result setText( "DEFEAT - you died" );
			}
		}

		// ---------------- mon etat -> fichier ----------------
		file_zone = my_zone;
		if ( file_zone == "" )
		{
			file_zone = "none";
		}

		file_time = elapsed;
		file_finished = 0;
		if ( finished )
		{
			file_time = finish_time;
			file_finished = 1;
		}
		if ( gave_up )
		{
			file_finished = 2;		// 2 = elimine (mort ou abandon)
		}

		seed = 0;
		if ( isDefined( level.rr_seed ) )
		{
			seed = level.rr_seed;
		}

		if ( files_write )
		{
			writeFile( "ranked/state.txt", my_round + ";" + file_zone + ";" + my_down + ";" + file_time + ";" + file_finished + ";" + finish_time + ";" + seed + ";" + goal + ";1" );
		}

		// ---------------- etat adverse -> HUD ----------------
		// En match : fichier ecrit par l'app. Hors match : dvars, pour tester a la main dans la console.
		if ( in_match )
		{
			if ( files_reads != 0 && fileExists( "ranked/opponent.txt" ) )
			{
				// Si le fichier est lu pendant que l'app l'ecrit, il peut etre incomplet :
				// dans ce cas on garde les valeurs precedentes.
				tokens = strTok( readFile( "ranked/opponent.txt" ), ";" );
				if ( tokens.size >= 4 )
				{
					opp_round = int( tokens[0] );
					opp_zone = tokens[1];
					if ( opp_zone == "none" )
					{
						opp_zone = "";
					}
					opp_down = int( tokens[2] );
					opp_finished = int( tokens[3] );
				}
			}

			// Fin du match : l'app supprime match.txt, puis tout le dossier quelques secondes plus tard.
			// Le mod arrete d'ecrire tout de suite (sinon il recreerait le dossier) et lit encore
			// deux fois l'etat adverse, pour ne pas manquer le resultat final.
			if ( files_write && !fileExists( "ranked/match.txt" ) )
			{
				files_write = false;
				files_reads = 2;
			}
			else if ( files_reads > 0 )
			{
				files_reads--;
			}
		}
		else
		{
			opp_round = getDvarInt( "ranked_opp_round" );
			opp_zone = getDvar( "ranked_opp_zone" );
			opp_down = getDvarInt( "ranked_opp_down" );
			opp_finished = getDvarInt( "ranked_opp_finished" );
		}

		if ( opp_round != last_round )
		{
			last_round = opp_round;
			if ( opp_round > 0 )
			{
				hud_round setText( "Opponent: round " + opp_round );
			}
			else
			{
				hud_round setText( "" );
			}
		}

		if ( opp_zone != last_zone )
		{
			last_zone = opp_zone;
			hud_zone setText( rr_zone_name( opp_zone ) );
		}

		if ( opp_down != last_state )
		{
			last_state = opp_down;
			if ( opp_down > 0 )
			{
				hud_state.color = ( 1, 0.3, 0.3 );
				hud_state setText( "DOWN" );
			}
			else
			{
				hud_state setText( "" );
			}
		}

		if ( opp_finished == 2 && !finished && !lost && !gave_up && !opp_out )
		{
			opp_out = true;
			hud_result.color = ( 0.3, 1, 0.3 );
			hud_result setText( "VICTORY - your opponent is out" );
		}

		if ( opp_finished == 1 && !finished && !lost && !gave_up && !opp_out )
		{
			lost = true;
			hud_result.color = ( 1, 0.3, 0.3 );
			hud_result setText( "DEFEAT - opponent reached round " + goal );
		}

		wait 0.5;
	}
}

// Depart synchronise. Le joueur est bloque au spawn, appuie sur une touche quand il est pret,
// et la partie demarre apres un compte a rebours quand l'app compagnon ecrit ranked/go.txt
// (c'est-a-dire quand le serveur a recu "pret" des deux joueurs).
// Si le match est annule (ranked/match.txt supprime par l'app), le joueur est libere sans compte a rebours.
rr_wait_for_go( player, goal )
{
	hud = NewClientHudElem( player );
	hud.foreground = true;
	hud.sort = 2;
	hud.hidewheninmenu = false;
	hud.alignX = "center";
	hud.alignY = "middle";
	hud.horzAlign = "center";
	hud.vertAlign = "middle";
	hud.x = 0;
	hud.y = -60;
	hud.fontScale = 2.2;
	hud.alpha = 1;
	hud.color = ( 1, 1, 1 );
	hud setText( "Press USE or FIRE when you are ready" );

	seed = 0;
	if ( isDefined( level.rr_seed ) )
	{
		seed = level.rr_seed;
	}

	ready = 0;
	cancelled = false;
	ticks = 0;

	while ( 1 )
	{
		player freezeControls( true );

		if ( !ready && ( player useButtonPressed() || player attackButtonPressed() ) )
		{
			ready = 1;
			hud.color = ( 0.3, 1, 0.3 );
			hud setText( "READY - waiting for your opponent" );
		}

		// Fichiers : toutes les 0.3 s seulement.
		if ( ticks % 3 == 0 )
		{
			writeFile( "ranked/state.txt", "1;none;0;0;0;0;" + seed + ";" + goal + ";" + ready );

			if ( ready && fileExists( "ranked/go.txt" ) )
			{
				break;
			}
			if ( !fileExists( "ranked/match.txt" ) )
			{
				cancelled = true;
				break;
			}
		}

		ticks++;
		wait 0.1;
	}

	if ( cancelled )
	{
		hud.color = ( 1, 0.3, 0.3 );
		hud setText( "Match cancelled" );
	}
	else
	{
		hud.color = ( 1, 0.8, 0.2 );
		hud.fontScale = 3;
		for ( count = 3; count > 0; count-- )
		{
			hud setText( "" + count );
			for ( i = 0; i < 10; i++ )
			{
				player freezeControls( true );
				wait 0.1;
			}
		}
		hud.color = ( 0.3, 1, 0.3 );
		hud setText( "GO!" );
	}

	player freezeControls( false );
	flag_set( "spawn_zombies" );

	hud thread rr_destroy_after( 2 );

	if ( !cancelled )
	{
		iPrintLn( "Type 'surrender' in the console to give up" );
	}
}

rr_cmd_surrender( args )
{
	level.rr_surrender = true;
}

// Fin de partie (mort du joueur en solo) = defaite.
rr_watch_game_over()
{
	level waittill( "end_game" );
	level.rr_dead = true;
}

rr_destroy_after( seconds )
{
	wait seconds;
	self destroy();
}

rr_flag_is_set( name )
{
	if ( !isDefined( level.flag ) || !isDefined( level.flag[name] ) )
	{
		return false;
	}
	return level.flag[name];
}

rr_link_hud( player, y, scale )
{
	hud = NewClientHudElem( player );
	hud.foreground = true;
	hud.sort = 1;
	hud.hidewheninmenu = true;
	hud.alignX = "left";
	hud.alignY = "top";
	hud.horzAlign = "user_left";
	hud.vertAlign = "user_top";
	hud.x = 8;
	hud.y = y;
	hud.fontScale = scale;
	hud.alpha = 1;
	hud.color = ( 1, 1, 1 );
	return hud;
}

rr_format_time( ms )
{
	total = int( ms / 1000 );
	minutes = int( total / 60 );
	seconds = total % 60;

	if ( seconds < 10 )
	{
		return minutes + ":0" + seconds;
	}
	return minutes + ":" + seconds;
}

// Nom lisible des zones. Kino pour l'instant ; une zone inconnue s'affiche telle quelle.
rr_zone_name( zone )
{
	switch ( zone )
	{
		case "":					return "";
		case "paused":				return "GAME PAUSED";
		case "foyer_zone":			return "Lobby";
		case "foyer2_zone":			return "Lobby (back)";
		case "vip_zone":			return "Upper Hall";
		case "dining_zone":			return "Dining Room";
		case "dressing_zone":		return "Dressing Room";
		case "stage_zone":			return "Stage";
		case "theater_zone":		return "Theater";
		case "crematorium_zone":	return "Crematorium";
		case "alleyway_zone":		return "Alley";
		case "west_balcony_zone":	return "West Balcony";
	}
	return zone;
}
